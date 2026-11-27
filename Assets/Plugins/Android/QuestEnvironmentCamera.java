// Copyright 2026 The Open Brush Authors. Licensed under the Apache License, Version 2.0.
package foundation.icosa.openbrush;

import android.content.Context;
import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.CaptureResult;
import android.hardware.camera2.TotalCaptureResult;
import android.hardware.camera2.params.OutputConfiguration;
import android.hardware.camera2.params.SessionConfiguration;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.LongSparseArray;
import android.util.Size;

import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.concurrent.atomic.AtomicReference;

/** A single Meta Passthrough Camera API stream, opened only on app request. */
public final class QuestEnvironmentCamera {
    private static final CameraCharacteristics.Key<Integer> CAMERA_SOURCE =
        new CameraCharacteristics.Key<>("com.meta.extra_metadata.camera_source", int.class);
    private static final CameraCharacteristics.Key<Integer> CAMERA_POSITION =
        new CameraCharacteristics.Key<>("com.meta.extra_metadata.position", int.class);
    private static final int PASSTHROUGH_SOURCE = 0;
    private static final int LEFT_CAMERA = 0;
    private static final int DOWNSAMPLE = 2;
    private static final long DELIVERY_INTERVAL_NS = 100_000_000L;
    private static final long MAX_FRAME_AGE_NS = 500_000_000L;

    private final CameraManager manager;
    private final HandlerThread thread;
    private final Handler handler;
    private final AtomicReference<Frame> latestFrame = new AtomicReference<>();
    private final LongSparseArray<TotalCaptureResult> captureResults = new LongSparseArray<>();
    private final byte[][] planeBytes = new byte[3][];

    private CameraDevice camera;
    private CameraCaptureSession session;
    private ImageReader reader;
    private boolean openPending;
    private long lastDeliveryNs;
    private boolean realtimeTimestamps;
    private int width;
    private int height;
    private float[] calibration;
    private volatile boolean closed;
    private volatile boolean running;
    private volatile String failure;
    private volatile String frameProblem;
    private volatile boolean alternatePermissionMayHelp;

    public QuestEnvironmentCamera(Context context) {
        manager = (CameraManager) context.getSystemService(Context.CAMERA_SERVICE);
        thread = new HandlerThread("Open Brush room camera");
        thread.start();
        handler = new Handler(thread.getLooper());
        handler.post(this::open);
    }

    private void open() {
        if (closed) {
            return;
        }
        try {
            for (String id : manager.getCameraIdList()) {
                CameraCharacteristics settings = manager.getCameraCharacteristics(id);
                Integer source = settings.get(CAMERA_SOURCE);
                Integer position = settings.get(CAMERA_POSITION);
                if (source != null && source == PASSTHROUGH_SOURCE &&
                    position != null && position == LEFT_CAMERA) {
                    configure(settings);
                    if (!closed) {
                        openPending = true;
                        manager.openCamera(id, cameraCallback, handler);
                    }
                    return;
                }
            }
            alternatePermissionMayHelp = true;
            fail("No supported Quest passthrough camera was found.");
        } catch (SecurityException error) {
            openPending = false;
            alternatePermissionMayHelp = true;
            fail("Camera2 access was denied: " + error.getMessage());
        } catch (Exception error) {
            openPending = false;
            fail("Couldn't open the Quest camera: " + error.getMessage());
        }
    }

    private void configure(CameraCharacteristics settings) {
        StreamConfigurationMap streams = settings.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Size[] sizes = streams == null ? null : streams.getOutputSizes(ImageFormat.YUV_420_888);
        if (sizes == null || sizes.length == 0) {
            throw new IllegalStateException("Camera has no readable video stream.");
        }
        // Keep the full 4:3 view used by the Quest camera rather than a narrower preview stream.
        Size size = sizes[0];
        for (Size option : sizes) {
            if (option.getWidth() == 1280 && option.getHeight() == 960) {
                size = option;
                break;
            }
        }
        width = size.getWidth() / DOWNSAMPLE;
        height = size.getHeight() / DOWNSAMPLE;

        float[] intrinsics = settings.get(CameraCharacteristics.LENS_INTRINSIC_CALIBRATION);
        float[] translation = settings.get(CameraCharacteristics.LENS_POSE_TRANSLATION);
        float[] rotation = settings.get(CameraCharacteristics.LENS_POSE_ROTATION);
        Rect sensor = settings.get(CameraCharacteristics.SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE);
        if (sensor == null || sensor.isEmpty()) {
            sensor = settings.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE);
        }
        if (sensor == null || sensor.isEmpty()) {
            Size pixels = settings.get(CameraCharacteristics.SENSOR_INFO_PIXEL_ARRAY_SIZE);
            if (pixels != null) {
                sensor = new Rect(0, 0, pixels.getWidth(), pixels.getHeight());
            }
        }
        if (intrinsics == null || intrinsics.length < 4 || intrinsics[0] <= 0f || intrinsics[1] <= 0f) {
            throw new IllegalStateException("Camera focal calibration is unavailable.");
        }
        if (translation == null || translation.length < 3 || rotation == null || rotation.length < 4) {
            throw new IllegalStateException("Camera tracking calibration is unavailable.");
        }
        if (sensor == null || sensor.isEmpty()) {
            throw new IllegalStateException("Camera sensor dimensions are unavailable.");
        }
        float streamScale = Math.max((float) size.getWidth() / sensor.width(),
                                     (float) size.getHeight() / sensor.height());
        float cropX = (sensor.width() - size.getWidth() / streamScale) * .5f;
        float cropY = (sensor.height() - size.getHeight() / streamScale) * .5f;
        float scale = streamScale / DOWNSAMPLE;
        calibration = new float[] {
            intrinsics[0] * scale, intrinsics[1] * scale,
            (intrinsics[2] - cropX) * scale, height - (intrinsics[3] - cropY) * scale,
            translation[0], translation[1], translation[2],
            rotation[0], rotation[1], rotation[2], rotation[3]
        };
        Integer clock = settings.get(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE);
        realtimeTimestamps = clock != null &&
            clock == CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME;
        reader = ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 3);
        reader.setOnImageAvailableListener(this::onImage, handler);
    }

    private final CameraDevice.StateCallback cameraCallback = new CameraDevice.StateCallback() {
        @Override public void onOpened(CameraDevice device) {
            openPending = false;
            if (closed) {
                device.close();
                thread.quitSafely();
                return;
            }
            camera = device;
            try {
                OutputConfiguration output = new OutputConfiguration(reader.getSurface());
                camera.createCaptureSession(new SessionConfiguration(SessionConfiguration.SESSION_REGULAR,
                    Collections.singletonList(output), handler::post, sessionCallback));
            } catch (Exception error) {
                fail("Couldn't configure the Quest camera: " + error.getMessage());
            }
        }

        @Override public void onDisconnected(CameraDevice device) {
            releaseDevice(device, "The Quest camera disconnected.");
        }

        @Override public void onError(CameraDevice device, int error) {
            releaseDevice(device, "The Quest camera reported error " + error + ".");
        }

        private void releaseDevice(CameraDevice device, String reason) {
            openPending = false;
            device.close();
            if (closed) {
                thread.quitSafely();
            } else {
                fail(reason);
            }
        }
    };

    private final CameraCaptureSession.StateCallback sessionCallback = new CameraCaptureSession.StateCallback() {
        @Override public void onConfigured(CameraCaptureSession configured) {
            if (closed) {
                configured.close();
                return;
            }
            session = configured;
            try {
                CaptureRequest.Builder request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                request.addTarget(reader.getSurface());
                session.setRepeatingRequest(request.build(), resultCallback, handler);
                running = true;
            } catch (Exception error) {
                fail("Couldn't start the Quest camera: " + error.getMessage());
            }
        }

        @Override public void onConfigureFailed(CameraCaptureSession configured) {
            configured.close();
            fail("The Quest camera rejected the video output.");
        }
    };

    private final CameraCaptureSession.CaptureCallback resultCallback = new CameraCaptureSession.CaptureCallback() {
        @Override public void onCaptureCompleted(CameraCaptureSession source, CaptureRequest request,
                                                 TotalCaptureResult result) {
            Long timestamp = result.get(CaptureResult.SENSOR_TIMESTAMP);
            if (timestamp == null || closed) {
                return;
            }
            Frame frame = latestFrame.get();
            if (frame != null && frame.timestampNs == timestamp) {
                frame.setExposure(result);
            } else {
                captureResults.put(timestamp, result);
                if (captureResults.size() > 8) {
                    captureResults.removeAt(0);
                }
            }
        }
    };

    private void onImage(ImageReader source) {
        if (closed) {
            return;
        }
        try (Image image = source.acquireLatestImage()) {
            long arrivalNs = SystemClock.elapsedRealtimeNanos();
            if (image == null || arrivalNs - lastDeliveryNs < DELIVERY_INTERVAL_NS) {
                return;
            }
            long timestampNs = image.getTimestamp();
            long alignedTimestampNs = shutterInElapsedClock(timestampNs, arrivalNs);
            if (alignedTimestampNs == 0) {
                frameProblem = "The Quest camera's capture timestamps could not be aligned to tracking.";
                return;
            }
            frameProblem = null;
            lastDeliveryNs = arrivalNs;
            Frame frame = new Frame(timestampNs, alignedTimestampNs, readRgb(image));
            TotalCaptureResult result = captureResults.get(timestampNs);
            if (result != null) {
                frame.setExposure(result);
                captureResults.remove(timestampNs);
            }
            if (!closed) {
                latestFrame.set(frame);
            }
        } catch (RuntimeException error) {
            fail("Couldn't read the Quest camera frame: " + error.getMessage());
        }
    }

    private long shutterInElapsedClock(long timestamp, long elapsedNow) {
        // Realtime Camera2 timestamps use BOOTTIME. Other Quest builds may report
        // camera timestamps on MONOTONIC, which excludes time spent suspended.
        long age = elapsedNow - timestamp;
        if (realtimeTimestamps && validAge(age)) {
            return timestamp;
        }
        long uptimeNow = System.nanoTime();
        long uptimeAge = uptimeNow - timestamp;
        if (validAge(uptimeAge)) {
            return elapsedNow - uptimeAge;
        }
        return validAge(age) ? timestamp : 0;
    }

    private static boolean validAge(long age) {
        return age >= 0 && age <= MAX_FRAME_AGE_NS;
    }

    private byte[] readRgb(Image image) {
        Image.Plane[] planes = image.getPlanes();
        // Copy each plane once before conversion; direct ByteBuffer reads per pixel are expensive.
        for (int i = 0; i < planes.length; i++) {
            ByteBuffer buffer = planes[i].getBuffer();
            int length = buffer.remaining();
            if (planeBytes[i] == null || planeBytes[i].length < length) {
                planeBytes[i] = new byte[length];
            }
            buffer.get(planeBytes[i], 0, length);
        }
        int yStride = planes[0].getPixelStride() * DOWNSAMPLE;
        int uStride = planes[1].getPixelStride();
        int vStride = planes[2].getPixelStride();
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++) {
            int yRow = y * DOWNSAMPLE * planes[0].getRowStride();
            int uRow = y * planes[1].getRowStride();
            int vRow = y * planes[2].getRowStride();
            // Android scans down from the top. Unity's first texture row is at the bottom.
            int dest = (height - 1 - y) * width * 3;
            for (int x = 0; x < width; x++) {
                int luma = Math.max(0, (planeBytes[0][yRow + x * yStride] & 255) - 16);
                int u = (planeBytes[1][uRow + x * uStride] & 255) - 128;
                int v = (planeBytes[2][vRow + x * vStride] & 255) - 128;
                // 8-bit studio-range YUV converted to gamma-encoded RGB.
                rgb[dest++] = component(298 * luma + 409 * v);
                rgb[dest++] = component(298 * luma - 100 * u - 208 * v);
                rgb[dest++] = component(298 * luma + 516 * u);
            }
        }
        return rgb;
    }

    private static byte component(int value) {
        return (byte) Math.min(255, Math.max(0, (value + 128) >> 8));
    }

    private void fail(String reason) {
        if (!closed) {
            failure = reason;
            close();
        }
    }

    public static long elapsedRealtimeNs() {
        return SystemClock.elapsedRealtimeNanos();
    }

    public boolean isRunning() { return running; }
    public String getFailure() { return failure; }
    public String getFrameProblem() { return frameProblem; }
    public boolean mayNeedAlternatePermission() { return alternatePermissionMayHelp; }
    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public float[] getCalibration() { return calibration; }
    public Frame takeLatestFrame() { return latestFrame.getAndSet(null); }

    public synchronized void close() {
        if (closed) {
            return;
        }
        closed = true;
        running = false;
        latestFrame.set(null);
        handler.post(() -> {
            if (session != null) {
                session.close();
            }
            if (camera != null) {
                camera.close();
            }
            if (reader != null) {
                reader.close();
            }
            captureResults.clear();
            latestFrame.set(null);
            // An in-flight open request can still deliver a device after cleanup.
            // Leave its callback alive so it can close that device.
            if (!openPending) {
                thread.quitSafely();
            }
        });
    }

    public static final class Frame {
        private final long timestampNs;
        private final long alignedTimestampNs;
        private final byte[] pixels;
        private volatile long exposureNs;
        private volatile int iso;

        Frame(long timestampNs, long alignedTimestampNs, byte[] pixels) {
            this.timestampNs = timestampNs;
            this.alignedTimestampNs = alignedTimestampNs;
            this.pixels = pixels;
        }

        void setExposure(TotalCaptureResult result) {
            Long exposure = result.get(CaptureResult.SENSOR_EXPOSURE_TIME);
            Integer sensitivity = result.get(CaptureResult.SENSOR_SENSITIVITY);
            exposureNs = exposure == null ? 0 : exposure;
            iso = sensitivity == null ? 0 : sensitivity;
        }

        public long getTimestampNs() { return timestampNs; }
        public long getAlignedTimestampNs() { return alignedTimestampNs; }
        public long getExposureNs() { return exposureNs; }
        public int getIso() { return iso; }
        public byte[] getPixels() { return pixels; }
    }
}
