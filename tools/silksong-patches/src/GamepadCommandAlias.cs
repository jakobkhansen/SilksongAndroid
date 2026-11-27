// GamepadCommandAlias -- pause and inventory on PlayStation and Xbox pads.
//
// On Android the game binds the inventory to InControl's Select and pause to
// Start, for every controller: InputHandler.SetActiveGamepadType only knows
// desktop platforms, logs "Unsupported platform for InputHander Android" and
// maps everything as an Xbox 360 pad.
//
// But the pad's own two middle buttons are not called Select and Start.
// NewUnityInputDevice registers them under its style's names, and the style
// comes from Unity's Android layout: a pad with Sony's or Microsoft's vendor id
// and Z/RZ/hat axes becomes DualShock4GamepadAndroid or XboxOneGamepadAndroid,
// so its buttons are Share/Options or View/Menu. A binding looks its control up
// by exact type, finds nothing, and the button is dead. Generic pads keep
// Select/Start and work, and so does anything the AYN firmware re-emits through
// its own uinput device (vendor 0x2020) -- which is why this never showed on a
// Thor, Bluetooth DualSense included.
//
// So the device gets the names the game asks for: Select and Start are pointed
// at the very InputControl objects the device already updates. Nothing new is
// polled and no binding is touched, so the game rebuilding its bindings on
// every controller change or remap cannot undo it. Done once per device, on
// attach.

#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Reflection;
using InControl;
using UnityEngine;

public class GamepadCommandAlias : MonoBehaviour
{
    const string Tag = "[GamepadCommandAlias] ";
    const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    // protected InputControl[] ControlsByTarget { get; private set; }
    static readonly PropertyInfo ByTargetProperty =
        typeof(InputDevice).GetProperty("ControlsByTarget", Priv);
    static readonly FieldInfo ByTargetField =
        typeof(InputDevice).GetField("<ControlsByTarget>k__BackingField", Priv);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("GamepadCommandAlias");
        DontDestroyOnLoad(go);
        go.AddComponent<GamepadCommandAlias>();
    }

    // Runs only until InControl is set up, and again after it resets: a reset
    // clears every subscriber to its events.
    void Update()
    {
        if (!InputManager.IsSetup) return;
        InputManager.OnDeviceAttached += Alias;
        InputManager.OnReset += OnInControlReset;
        foreach (InputDevice device in InputManager.Devices) Alias(device);
        enabled = false;
    }

    void OnInControlReset()
    {
        enabled = true;
    }

    static void Alias(InputDevice device)
    {
        try
        {
            InputDeviceStyle style = device.DeviceStyle;
            int left = Alias(device, style.LeftCommandControl(), InputControlType.Select);
            int right = Alias(device, style.RightCommandControl(), InputControlType.Start);
            if (left + right > 0)
                Debug.Log(Tag + "'" + device.Name + "' (" + style + "): Select = "
                          + style.LeftCommandControl() + ", Start = " + style.RightCommandControl());
        }
        catch (Exception e)
        {
            Debug.LogWarning(Tag + e);
        }
    }

    static int Alias(InputDevice device, InputControlType source, InputControlType target)
    {
        if (source == target || !device.HasControl(source) || device.HasControl(target)) return 0;
        InputControl[] byTarget = ControlsByTarget(device);
        if (byTarget == null) return 0;
        byTarget[(int)target] = byTarget[(int)source];
        return 1;
    }

    static InputControl[] ControlsByTarget(InputDevice device)
    {
        if (ByTargetProperty != null) return (InputControl[])ByTargetProperty.GetValue(device, null);
        if (ByTargetField != null) return (InputControl[])ByTargetField.GetValue(device);
        Debug.LogWarning(Tag + "InputDevice.ControlsByTarget not found");
        return null;
    }
}
#endif
