using System;

namespace Argus.Simulation.Core
{
    // Append new values only; never renumber. Mirrors the argus.sim.v1.DeviceValue oneof.
    public enum DeviceValueType
    {
        Unset = 0,
        Number = 1,
        Vector3 = 2,
        Quaternion = 3,
        Matrix3 = 4,
        Flag = 5,
        Text = 6
    }

    // One typed value of a device's parameters, initial state or output (D11, docs/device-model.md
    // §3). The kind declares each key's type; reading a value as another type throws.
    public readonly struct DeviceValue
    {
        private readonly double _number;
        private readonly Vector3d _vector3;
        private readonly Quaterniond _quaternion;
        private readonly Matrix3d _matrix3;
        private readonly bool _flag;
        private readonly string _text;

        private DeviceValue(
            DeviceValueType type,
            double number = 0.0,
            Vector3d vector3 = default,
            Quaterniond quaternion = default,
            Matrix3d matrix3 = default,
            bool flag = false,
            string text = null)
        {
            Type = type;
            _number = number;
            _vector3 = vector3;
            _quaternion = quaternion;
            _matrix3 = matrix3;
            _flag = flag;
            _text = text;
        }

        public DeviceValueType Type { get; }

        public double Number => Type == DeviceValueType.Number ? _number : throw WrongType(DeviceValueType.Number);
        public Vector3d Vector3 => Type == DeviceValueType.Vector3 ? _vector3 : throw WrongType(DeviceValueType.Vector3);
        public Quaterniond Quaternion => Type == DeviceValueType.Quaternion ? _quaternion : throw WrongType(DeviceValueType.Quaternion);
        public Matrix3d Matrix3 => Type == DeviceValueType.Matrix3 ? _matrix3 : throw WrongType(DeviceValueType.Matrix3);
        public bool Flag => Type == DeviceValueType.Flag ? _flag : throw WrongType(DeviceValueType.Flag);
        public string Text => Type == DeviceValueType.Text ? _text : throw WrongType(DeviceValueType.Text);

        // Every number in the value is finite; a flag or text always is.
        public bool IsFinite => Type switch
        {
            DeviceValueType.Number => !double.IsNaN(_number) && !double.IsInfinity(_number),
            DeviceValueType.Vector3 => _vector3.IsFinite,
            DeviceValueType.Quaternion => _quaternion.IsFinite,
            DeviceValueType.Matrix3 => _matrix3.IsFinite,
            DeviceValueType.Flag => true,
            DeviceValueType.Text => _text != null,
            _ => false
        };

        public static DeviceValue FromNumber(double value) => new DeviceValue(DeviceValueType.Number, number: value);
        public static DeviceValue FromVector3(Vector3d value) => new DeviceValue(DeviceValueType.Vector3, vector3: value);
        public static DeviceValue FromQuaternion(Quaterniond value) => new DeviceValue(DeviceValueType.Quaternion, quaternion: value);
        public static DeviceValue FromMatrix3(Matrix3d value) => new DeviceValue(DeviceValueType.Matrix3, matrix3: value);
        public static DeviceValue FromFlag(bool value) => new DeviceValue(DeviceValueType.Flag, flag: value);
        public static DeviceValue FromText(string value) => new DeviceValue(DeviceValueType.Text, text: value);

        private InvalidOperationException WrongType(DeviceValueType requested) =>
            new InvalidOperationException($"The device value is a {Type}, not a {requested}.");
    }
}
