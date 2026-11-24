// Copyright 2020 The Tilt Brush Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using UnityEngine;

namespace TiltBrush
{
    public class ActionButton : BaseButton
    {
        private const float kRepeatDelay = 1f;
        private const float kRepeatInterval = 0.125f;

        [SerializeField] private UnityEngine.Events.UnityEvent m_Action;
        [SerializeField] private bool m_RepeatOnHold;

        private float m_NextRepeatTime;
        private int m_LastHeldFrame;

        private bool RepeatsOnHold => m_RepeatOnHold && !m_LongPressReleaseButton;

        protected override void Awake()
        {
            base.Awake();
            if (RepeatsOnHold)
                m_HoldFocus = false;
        }

        public override void ButtonPressed(RaycastHit hitInfo)
        {
            if (RepeatsOnHold)
            {
                if (!InputManager.m_Instance.GetCommandDown(InputManager.SketchCommands.Activate))
                    return;

                m_NextRepeatTime = Time.unscaledTime + kRepeatDelay;
                m_LastHeldFrame = Time.frameCount;
            }
            base.ButtonPressed(hitInfo);
        }

        public override void ButtonHeld(RaycastHit hitInfo)
        {
            if (!RepeatsOnHold || !IsPressed())
                return;

            // When the panel itself loses focus, it may stop updating before resetting the button.
            int frame = Time.frameCount;
            if (frame > m_LastHeldFrame + 1)
            {
                ResetState();
                return;
            }
            m_LastHeldFrame = frame;

            float now = Time.unscaledTime;
            if (now >= m_NextRepeatTime)
            {
                m_NextRepeatTime = now + kRepeatInterval;
                OnButtonPressed();
            }
        }

        protected override void OnDisable()
        {
            if (RepeatsOnHold)
                ResetState();
            base.OnDisable();
        }

        protected override void OnButtonPressed()
        {
            m_Action.Invoke();
        }
    }
} // namespace TiltBrush
