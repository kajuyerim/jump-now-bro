using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using JumpNowBro.Gameplay;

namespace JumpNowBro.Tests.PlayMode
{
    public class InputRebindTests
    {
        [UnityTest]
        public IEnumerator CancelKeepsBindingAndPanelOpen_AndEastRemainsBindable()
        {
            var actions = InputSystem.actions;
            Assert.IsNotNull(actions);
            var panel = SettingsPanel.Instance;
            Assert.IsNotNull(panel);
            string overrides = actions.SaveBindingOverridesAsJson();
            bool hadPrefs = PlayerPrefs.HasKey("input.overrides");
            string savedPrefs = GameSettings.InputOverrides;
            bool wasOpen = panel.IsOpen;
            var previousPad = Gamepad.current;
            var previousKeyboard = Keyboard.current;
            var jump = InputRebinds.Cells().First(c => c.Gamepad && c.Action.name == "Jump");
            bool wasEnabled = jump.Action.enabled;
            var pad = InputSystem.AddDevice<Gamepad>();
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                actions.RemoveAllBindingOverrides();
                panel.Open();
                jump.Action.Enable();
                int completed = 0;
                string error = null;
                InputRebinds.StartRebind(jump, e => { completed++; error = e; });
                InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.Start));
                yield return null;
                Assert.IsFalse(InputRebinds.Listening);
                Assert.AreEqual(1, completed);
                Assert.IsNull(error);
                Assert.IsNull(jump.Action.bindings[jump.BindingIndex].overridePath);
                Assert.IsTrue(jump.Action.enabled);
                Assert.IsTrue(panel.IsOpen, "cancel must not also toggle Settings closed");
                Assert.AreEqual(savedPrefs, GameSettings.InputOverrides, "cancel must not save a binding");

                InputSystem.QueueStateEvent(pad, new GamepadState());
                yield return null;
                InputRebinds.StartRebind(jump, e => { completed++; error = e; });
                InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.East));
                float deadline = Time.realtimeSinceStartup + 2f;
                while (InputRebinds.Listening && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsFalse(InputRebinds.Listening);
                Assert.AreEqual(2, completed);
                Assert.IsNull(error);
                Assert.AreEqual("<Gamepad>/buttonEast", jump.Action.bindings[jump.BindingIndex].overridePath);
                Assert.IsTrue(panel.IsOpen);

                var key = InputRebinds.Cells().First(c => !c.Gamepad);
                InputRebinds.StartRebind(key, _ => completed++);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                yield return null;
                Assert.IsFalse(InputRebinds.Listening);
                Assert.AreEqual(3, completed);
                Assert.IsNull(key.Action.bindings[key.BindingIndex].overridePath);
                Assert.IsTrue(panel.IsOpen);
            }
            finally
            {
                // Also unwind a failed assertion while a rebind is still listening.
                InputSystem.QueueStateEvent(pad, new GamepadState());
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.Update();
                InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.Start));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                InputSystem.Update();
                actions.LoadBindingOverridesFromJson(overrides);
                if (!wasEnabled) jump.Action.Disable();
                if (hadPrefs) GameSettings.SetInputOverrides(savedPrefs);
                else PlayerPrefs.DeleteKey("input.overrides");
                if (!wasOpen) panel.Close();
                InputSystem.RemoveDevice(pad);
                InputSystem.RemoveDevice(keyboard);
                previousPad?.MakeCurrent();
                previousKeyboard?.MakeCurrent();
            }
        }
    }
}
