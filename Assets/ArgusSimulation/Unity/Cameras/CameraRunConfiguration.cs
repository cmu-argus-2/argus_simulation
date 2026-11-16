using System;
using System.Collections.Generic;
using System.IO;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Reads the camera subset of the same run file supplied to the headless host. The stream does
    // not carry run configuration, so a different host run must explicitly select the same file.
    internal static class CameraRunConfiguration
    {
        public static string ResolvePath(string inspectorPath)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--run-config") return Path.GetFullPath(args[i + 1]);
            string path = Environment.GetEnvironmentVariable("ARGUS_RUN_CONFIG") ?? inspectorPath;
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Application.dataPath, "..", path));
        }

        public static Dictionary<string, DeviceConfiguration> Load(string path)
        {
            RunFile run = JsonUtility.FromJson<RunFile>(File.ReadAllText(path));
            var devices = new Dictionary<string, DeviceConfiguration>(StringComparer.Ordinal);
            foreach (DeviceFile device in run.devices ?? Array.Empty<DeviceFile>())
            {
                if (device.kind != CameraKind.KindName) continue;
                Parameters p = device.parameters ?? throw new InvalidDataException("Camera parameters are missing: " + device.deviceId);
                Intrinsics k = p.intrinsics ?? throw new InvalidDataException("Camera intrinsics are missing: " + device.deviceId);
                if (p.distortion == null || p.noise == null || p.extrinsics == null || device.mount == null ||
                    device.mount.positionBodyMeters?.Length != 3 || device.mount.deviceToBody == null)
                    throw new InvalidDataException("Incomplete camera profile or mount: " + device.deviceId);
                var profile = new CameraProfile(p.profile_id,
                    new CameraIntrinsics(k.width_px, k.height_px, k.focal_length_x_px, k.focal_length_y_px,
                        k.principal_point_x_px, k.principal_point_y_px),
                    k.source, k.calibrated,
                    new CameraDistortion(p.distortion.model, p.distortion.k1, p.distortion.k2, p.distortion.p1,
                        p.distortion.p2, p.distortion.k3, p.distortion.source),
                    p.noise.source, p.exposure_duration_s,
                    new CameraExtrinsicsProvenance(p.extrinsics.source, p.extrinsics.origin, p.extrinsics.orientation_verified));
                double[] position = device.mount.positionBodyMeters;
                Rotation q = device.mount.deviceToBody;
                var definition = new SensorDefinition(device.deviceId, device.modelName, device.frameId,
                    device.samplePeriodSeconds, "argus", new SensorMount(new Vector3d(position[0], position[1], position[2]),
                        new Quaterniond(q.x, q.y, q.z, q.w)), device.controllerVisible);
                DeviceConfiguration configured = CameraKind.Instance.Configure(definition, profile);
                if (!configured.IsValid) throw new InvalidDataException("Invalid camera " + device.deviceId + ": " + configured.FindProblem());
                devices.Add(device.deviceId, configured);
            }
            return devices;
        }

#pragma warning disable CS0649 // JsonUtility populates these run-file fields.
        [Serializable] private sealed class RunFile { public DeviceFile[] devices; }
        [Serializable] private sealed class DeviceFile
        {
            public string deviceId, kind, modelName, frameId;
            public double samplePeriodSeconds;
            public bool controllerVisible;
            public Mount mount;
            public Parameters parameters;
        }
        [Serializable] private sealed class Mount { public double[] positionBodyMeters; public Rotation deviceToBody; }
        [Serializable] private sealed class Rotation { public double x, y, z, w; }
        [Serializable] private sealed class Parameters
        {
            public string profile_id;
            public Intrinsics intrinsics;
            public Distortion distortion;
            public Noise noise;
            public double exposure_duration_s;
            public Extrinsics extrinsics;
        }
        [Serializable] private sealed class Intrinsics
        {
            public int width_px, height_px;
            public double focal_length_x_px, focal_length_y_px, principal_point_x_px, principal_point_y_px;
            public string source;
            public bool calibrated;
        }
        [Serializable] private sealed class Distortion { public string model, source; public double k1, k2, p1, p2, k3; }
        [Serializable] private sealed class Noise { public string source; }
        [Serializable] private sealed class Extrinsics { public string source, origin; public bool orientation_verified; }
#pragma warning restore CS0649
    }
}
