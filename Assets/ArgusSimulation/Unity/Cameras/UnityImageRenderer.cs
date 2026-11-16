using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Requests may arrive off-thread; Unity objects are touched only by the main-thread pump.
    // One capture at a time owns the request camera, render target and night-side override.
    [DisallowMultipleComponent]
    public sealed class UnityImageRenderer : MonoBehaviour, IImageRenderer
    {
        [SerializeField] private CubeSatCameraRig cameraRig;
        [SerializeField] private CesiumGeoreference georeference;
        [SerializeField] private Light sunlight;
        [SerializeField] private NasaGibsRasterController rasterController;
        [SerializeField, Range(90f, 100f)] private float minimumLoadProgress = 100f;
        [SerializeField, Min(1)] private int stableFramesRequired = 10;
        [SerializeField, Min(0f)] private float postLoadSettleSeconds = 1f;
        [SerializeField, Min(1f)] private float loadTimeoutSeconds = 60f;
        [SerializeField, Min(1f)] private float maximumLoadSeconds = 1200f;
        [SerializeField, Min(2)] private int readinessProbeIntervalFrames = 2;

        private readonly object _gate = new object();
        private readonly Queue<PendingRender> _requests = new Queue<PendingRender>();
        private bool _accepting;
        private PendingRender _active;
        private IEnumerator _capture;
        private Coroutine _pump;

        public string RendererName => "Unity/Cesium";
        public string CaptureStatus { get; private set; } = "IDLE";
        public int ReadinessProbeCount { get; private set; }
        public int MaximumReadinessProbeFrames { get; private set; }
        public event Action<string> CaptureStatusChanged;

        private void SetCaptureStatus(string status)
        {
            if (CaptureStatus == status) return;
            CaptureStatus = status;
            CaptureStatusChanged?.Invoke(status);
        }
        public bool IsRendering
        {
            get
            {
                lock (_gate)
                {
                    if (_active != null) return true;
                    foreach (PendingRender pending in _requests)
                        if (!pending.Completion.Task.IsCompleted) return true;
                    return false;
                }
            }
        }

        public void Configure(CubeSatCameraRig rig, CesiumGeoreference reference,
            Light source = null, NasaGibsRasterController raster = null)
        {
            cameraRig = rig;
            georeference = reference;
            sunlight = source;
            rasterController = raster;
        }

        public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken) =>
            Enqueue(request, cancellationToken, false);

        internal Task<ImageFrame> RenderPngAsync(RenderRequest request, CancellationToken cancellationToken) =>
            Enqueue(request, cancellationToken, true);

        private Task<ImageFrame> Enqueue(RenderRequest request, CancellationToken cancellationToken, bool encodePng)
        {
            if (!request.IsValid)
                return Task.FromException<ImageFrame>(new ArgumentException("A valid render request is required."));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<ImageFrame>(cancellationToken);

            var pending = new PendingRender(request, cancellationToken, encodePng);
            lock (_gate)
            {
                if (!_accepting)
                    return Task.FromException<ImageFrame>(new InvalidOperationException("Renderer is disabled."));
                pending.Registration = cancellationToken.Register(() =>
                {
                    lock (_gate)
                        if (!pending.IsActive) pending.Completion.TrySetCanceled(cancellationToken);
                });
                _requests.Enqueue(pending);
            }
            return pending.Completion.Task;
        }

        private void OnEnable() { lock (_gate) _accepting = true; }

        private void Update()
        {
            while (true)
            {
                PendingRender pending;
                lock (_gate)
                {
                    if (_active != null || _requests.Count == 0) return;
                    pending = _requests.Dequeue();
                    if (!pending.Completion.Task.IsCompleted)
                    {
                        pending.IsActive = true;
                        _active = pending;
                    }
                }
                if (_active != null) break;
                pending.Registration.Dispose();
            }
            _capture = Capture(_active);
            _pump = StartCoroutine(Pump());
        }

        // Complete only after iterator disposal has restored all scene resources.
        private IEnumerator Pump()
        {
            Exception failure = null;
            while (true)
            {
                bool next;
                try { next = _capture.MoveNext(); }
                catch (Exception exception) { failure = exception; break; }
                if (!next) break;
                yield return _capture.Current;
            }
            try { (_capture as IDisposable)?.Dispose(); }
            catch (Exception exception) { failure = failure == null ? exception : new AggregateException(failure, exception); }
            PendingRender pending = _active;
            _capture = null;
            lock (_gate) _active = null;
            _pump = null;
            pending.Registration.Dispose();
            SetCaptureStatus("IDLE");
            if (failure is OperationCanceledException) pending.Completion.TrySetCanceled(pending.Token);
            else if (failure != null) pending.Completion.TrySetException(failure);
            else pending.Completion.TrySetResult(pending.Frame);
        }

        private IEnumerator Capture(PendingRender pending)
        {
            pending.Token.ThrowIfCancellationRequested();
            ResolveScene();
            RenderRequest request = pending.Request;
            Camera camera = cameraRig.RequestCamera;
            RenderTexture target = null;
            CesiumCameraManager manager = CesiumCameraManager.GetOrCreate(cameraRig.gameObject);
            bool registered = false;
            try
            {
                target = new RenderTexture(request.Intrinsics.WidthPixels, request.Intrinsics.HeightPixels,
                    24, RenderTextureFormat.ARGB32) { name = "Core camera exposure", antiAliasing = 1 };
                if (!target.Create()) throw new InvalidOperationException("Could not allocate the exposure render target.");
                camera.targetTexture = target;
                // Cesium selects tiles from Camera.fieldOfView/aspect, not the custom projection.
                // Give it a symmetric frustum enclosing the exact, possibly off-centre image.
                CameraIntrinsics k = request.Intrinsics;
                double halfX = Math.Max(Math.Abs(k.PrincipalPointXPixels + 0.5),
                    Math.Abs(k.WidthPixels - k.PrincipalPointXPixels - 0.5)) / k.FocalLengthXPixels;
                double halfY = Math.Max(Math.Abs(k.PrincipalPointYPixels + 0.5),
                    Math.Abs(k.HeightPixels - k.PrincipalPointYPixels - 0.5)) / k.FocalLengthYPixels;
                camera.aspect = (float)k.WidthPixels / k.HeightPixels;
                camera.fieldOfView = (float)(2 * Math.Atan(Math.Max(halfY, halfX / camera.aspect)) * 180 / Math.PI);
                camera.projectionMatrix = Projection(request.Intrinsics, camera.nearClipPlane, camera.farClipPlane);
                ApplyPose(camera, request);
                // Register alongside live views; only readiness probes select the request alone.
                manager.additionalCameras.Add(camera);
                registered = true;

                Vector3? towardSun = null;
                if (request.HasSunPosition)
                {
                    if (sunlight == null || sunlight.type != LightType.Directional)
                        throw new InvalidOperationException("Sun-lit requests require a directional Sun light.");
                    towardSun = ToWorldDirection(request.SunPositionEcefMeters.Value - request.CameraPositionEcefMeters).normalized;
                    if (towardSun.Value.sqrMagnitude == 0f)
                        throw new InvalidOperationException("The Sun direction is undefined at the camera position.");
                }

                Cesium3DTileset[] tilesets = FindObjectsByType<Cesium3DTileset>(FindObjectsInactive.Exclude);
                float startedAt = Time.realtimeSinceStartup;
                float lastProgressAt = startedAt;
                float lastProgress = -1f;
                int stable = 0;
                // Start probes after Update so one yielded frame covers one Cesium selection update.
                yield return null;
                while (stable < Math.Max(1, stableFramesRequired))
                {
                    pending.Token.ThrowIfCancellationRequested();
                    // Reapply after origin shifts; poses are Earth-fixed, not stale Unity positions.
                    ApplyPose(camera, request);
                    bool hasTilesets = Array.Exists(tilesets, tileset => tileset != null && tileset.isActiveAndEnabled);
                    float progress;
                    if (hasTilesets)
                    {
                        // Cesium exposes only aggregate readiness. Probe this exposure for one
                        // selection frame, then give every live view the rest of the interval.
                        int probeStart = Time.frameCount;
                        try
                        {
                            using (var probe = new CaptureViewProbe(manager))
                            {
                                probe.Select(camera);
                                yield return null;
                                pending.Token.ThrowIfCancellationRequested();
                                progress = MinimumEnabledTilesetProgress(tilesets);
                            }
                        }
                        finally
                        {
                            ReadinessProbeCount++;
                            MaximumReadinessProbeFrames = Math.Max(MaximumReadinessProbeFrames, Time.frameCount - probeStart);
                        }
                    }
                    else progress = 100f;
                    float now = Time.realtimeSinceStartup;
                    if (Mathf.Abs(progress - lastProgress) >= 0.5f)
                    {
                        lastProgress = progress;
                        lastProgressAt = now;
                    }
                    SetCaptureStatus($"LOADING {progress:0}% ({now - startedAt:0}s)");
                    stable = progress >= minimumLoadProgress ? stable + 1 : 0;
                    if (now - lastProgressAt >= Math.Max(1f, loadTimeoutSeconds) ||
                        now - startedAt >= Math.Max(1f, maximumLoadSeconds))
                        throw new TimeoutException($"Cesium view is at {progress:0}% after {now - startedAt:0}s " +
                            $"({now - lastProgressAt:0}s without progress); check terrain/network errors and retry.");
                    int liveFrames = hasTilesets ? Math.Max(2, readinessProbeIntervalFrames) - 1 : 1;
                    for (int frame = 0; frame < liveFrames; frame++)
                    {
                        pending.Token.ThrowIfCancellationRequested();
                        ApplyPose(camera, request);
                        yield return null;
                    }
                }
                SetCaptureStatus("SETTLING");
                float settleUntil = Time.realtimeSinceStartup + Math.Max(0f, postLoadSettleSeconds);
                while (Time.realtimeSinceStartup < settleUntil)
                {
                    pending.Token.ThrowIfCancellationRequested();
                    ApplyPose(camera, request);
                    yield return null;
                }
                pending.Token.ThrowIfCancellationRequested();
                ApplyPose(camera, request);
                Quaternion originalLightRotation = sunlight == null ? Quaternion.identity : sunlight.transform.rotation;
                // A far-from-origin view/projection can become singular in Unity's float math,
                // even though the ECEF request is valid. Rebase only for the synchronous render.
                bool rebase = camera.transform.position.sqrMagnitude > 100_000f * 100_000f;
                var originalOrigin = new double3(georeference.ecefX, georeference.ecefY, georeference.ecefZ);
                var originalLlh = new double3(georeference.longitude, georeference.latitude, georeference.height);
                var originalAuthority = georeference.originAuthority;
                var originalPlacement = georeference.originPlacement;
                Vector3d lightForward = sunlight == null ? default : ToEcefDirection(sunlight.transform.forward);
                Vector3d lightUp = sunlight == null ? default : ToEcefDirection(sunlight.transform.up);
                Texture2D image = null;
                try
                {
                    if (rebase)
                    {
                        Vector3d p = request.CameraPositionEcefMeters;
                        georeference.SetOriginEarthCenteredEarthFixed(p.X, p.Y, p.Z);
                        georeference.originPlacement = CesiumGeoreferenceOriginPlacement.CartographicOrigin;
                        ApplyPose(camera, request);
                        if (sunlight != null)
                            sunlight.transform.rotation = Quaternion.LookRotation(ToWorldDirection(lightForward), ToWorldDirection(lightUp));
                    }
                    if (towardSun.HasValue)
                    {
                        Vector3 direction = ToWorldDirection(request.SunPositionEcefMeters.Value - request.CameraPositionEcefMeters).normalized;
                        rasterController?.SetCaptureSunDirection(direction);
                        sunlight.transform.rotation = Quaternion.LookRotation(-direction,
                            Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up);
                    }
                    SetCaptureStatus("READING PIXELS");
                    camera.Render();
                    image = ReadRenderTexture(target);
                    pending.Token.ThrowIfCancellationRequested();
                    if (pending.EncodePng)
                        pending.Frame = new ImageFrame(request, ImageEncoding.Png, image.EncodeToPNG(), RendererName);
                    else
                    {
                        byte[] pixels = image.GetRawTextureData<byte>().ToArray();
                        FlipRows(pixels, request.Intrinsics.WidthPixels * 3, request.Intrinsics.HeightPixels);
                        pending.Frame = new ImageFrame(request, ImageEncoding.Rgb8, pixels, RendererName);
                    }
                }
                finally
                {
                    RestoreAll(
                        () =>
                        {
                            if (!rebase) return;
                            RestoreAll(
                                () =>
                                {
                                    if (originalAuthority == CesiumGeoreferenceOriginAuthority.LongitudeLatitudeHeight)
                                        georeference.SetOriginLongitudeLatitudeHeight(originalLlh.x, originalLlh.y, originalLlh.z);
                                    else georeference.SetOriginEarthCenteredEarthFixed(originalOrigin.x, originalOrigin.y, originalOrigin.z);
                                },
                                () => georeference.originPlacement = originalPlacement,
                                () => ApplyPose(camera, request));
                        },
                        () => { if (sunlight != null) sunlight.transform.rotation = originalLightRotation; },
                        () => rasterController?.SetCaptureSunDirection(null),
                        () => { if (image != null) Destroy(image); });
                }
            }
            finally
            {
                RestoreAll(
                    () => { if (registered && manager != null) manager.additionalCameras.Remove(camera); },
                    () => { if (camera != null) camera.targetTexture = null; },
                    () => { if (camera != null) camera.ResetProjectionMatrix(); },
                    () => { if (target != null) { try { target.Release(); } finally { Destroy(target); } } });
            }
        }

        // Scope is disposed before any error/cancellation leaves the one-frame readiness probe.
        private sealed class CaptureViewProbe : IDisposable
        {
            private readonly CesiumCameraManager _manager;
            private readonly List<Camera> _views;
            private readonly bool _main, _scene;

            public CaptureViewProbe(CesiumCameraManager manager)
            {
                _manager = manager;
                _views = new List<Camera>(manager.additionalCameras);
                _main = manager.useMainCamera;
                _scene = manager.useSceneViewCameraInEditor;
            }

            public void Select(Camera camera)
            {
                _manager.useMainCamera = false;
                _manager.useSceneViewCameraInEditor = false;
                _manager.additionalCameras.Clear();
                _manager.additionalCameras.Add(camera);
            }

            public void Dispose()
            {
                if (_manager == null) return;
                RestoreAll(
                    () =>
                    {
                        foreach (Camera view in _views)
                            if (view != null && !_manager.additionalCameras.Contains(view)) _manager.additionalCameras.Add(view);
                    },
                    () => _manager.useMainCamera = _main,
                    () => _manager.useSceneViewCameraInEditor = _scene);
            }
        }

        // Attempt every restore even if an earlier operation fails; propagate the complete failure.
        internal static void RestoreAll(params Action[] actions)
        {
            List<Exception> failures = null;
            foreach (Action action in actions)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= new List<Exception>()).Add(exception); }
            }
            if (failures != null) throw new AggregateException("Camera scene restoration failed.", failures);
        }

        private void ResolveScene()
        {
            if (cameraRig == null) cameraRig = FindAnyObjectByType<CubeSatCameraRig>();
            if (georeference == null && cameraRig != null) georeference = cameraRig.GetComponentInParent<CesiumGeoreference>();
            if (cameraRig == null || georeference == null)
                throw new InvalidOperationException("A camera rig under a CesiumGeoreference is required.");
            if (sunlight == null)
                foreach (Light candidate in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                    if (candidate.type == LightType.Directional) { sunlight = candidate; break; }
            if (rasterController == null) rasterController = FindAnyObjectByType<NasaGibsRasterController>();
        }

        private void ApplyPose(Camera camera, RenderRequest request)
        {
            Vector3d p = request.CameraPositionEcefMeters;
            double3 local = georeference.TransformEarthCenteredEarthFixedPositionToUnity(new double3(p.X, p.Y, p.Z));
            camera.transform.position = georeference.transform.TransformPoint(new Vector3((float)local.x, (float)local.y, (float)local.z));
            // OpenCV: x right, y down, z forward. Unity camera: x right, y up, z forward.
            // Convert basis directions, including the handedness change in Cesium's mapping.
            Vector3 forward = ToWorldDirection(request.CameraToEcef.Rotate(new Vector3d(0, 0, 1)));
            Vector3 up = ToWorldDirection(request.CameraToEcef.Rotate(new Vector3d(0, -1, 0)));
            camera.transform.rotation = Quaternion.LookRotation(forward, up);
        }

        private Vector3 ToWorldDirection(Vector3d ecef)
        {
            double3 local = georeference.TransformEarthCenteredEarthFixedDirectionToUnity(new double3(ecef.X, ecef.Y, ecef.Z));
            return georeference.transform.TransformDirection(new Vector3((float)local.x, (float)local.y, (float)local.z));
        }

        private Vector3d ToEcefDirection(Vector3 worldDirection)
        {
            Vector3 local = georeference.transform.InverseTransformDirection(worldDirection);
            double3 ecef = georeference.TransformUnityDirectionToEarthCenteredEarthFixed(new double3(local.x, local.y, local.z));
            return new Vector3d(ecef.x, ecef.y, ecef.z).Normalized();
        }

        // Pixel (0,0) is the centre of the top-left pixel; the image edges lie at -0.5.
        internal static Matrix4x4 Projection(CameraIntrinsics k, float near, float far)
        {
            Matrix4x4 matrix = Matrix4x4.zero;
            matrix.m00 = (float)(2 * k.FocalLengthXPixels / k.WidthPixels);
            matrix.m11 = (float)(2 * k.FocalLengthYPixels / k.HeightPixels);
            matrix.m02 = (float)(1 - 2 * (k.PrincipalPointXPixels + 0.5) / k.WidthPixels);
            matrix.m12 = (float)(2 * (k.PrincipalPointYPixels + 0.5) / k.HeightPixels - 1);
            matrix.m22 = -(far + near) / (far - near);
            matrix.m23 = -2 * far * near / (far - near);
            matrix.m32 = -1;
            return matrix;
        }

        internal static float MinimumEnabledTilesetProgress(Cesium3DTileset[] tilesets)
        {
            float progress = 100f;
            foreach (Cesium3DTileset tileset in tilesets)
            {
                if (tileset == null || !tileset.isActiveAndEnabled) continue;
                float value = tileset.ComputeLoadProgress();
                progress = Math.Min(progress, float.IsNaN(value) ? 0f : value);
            }
            return progress;
        }

        private static Texture2D ReadRenderTexture(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                RenderTexture.active = target;
                image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                image.Apply();
                return image;
            }
            catch { if (image != null) Destroy(image); throw; }
            finally { RenderTexture.active = previous; }
        }

        internal static byte[] EncodePng(ImageFrame frame)
        {
            if (frame.Encoding == ImageEncoding.Png) return frame.Data;
            byte[] bottomUp = (byte[])frame.Data.Clone();
            FlipRows(bottomUp, frame.Intrinsics.WidthPixels * 3, frame.Intrinsics.HeightPixels);
            Texture2D image = new Texture2D(frame.Intrinsics.WidthPixels, frame.Intrinsics.HeightPixels, TextureFormat.RGB24, false);
            try { image.LoadRawTextureData(bottomUp); image.Apply(); return image.EncodeToPNG(); }
            finally { Destroy(image); }
        }

        private static void FlipRows(byte[] pixels, int stride, int height)
        {
            byte[] row = new byte[stride];
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * stride, bottom = (height - 1 - y) * stride;
                Buffer.BlockCopy(pixels, top, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom, stride);
            }
        }

        private void OnDisable()
        {
            var queued = new List<PendingRender>();
            lock (_gate)
            {
                _accepting = false;
                while (_requests.Count > 0) queued.Add(_requests.Dequeue());
            }
            foreach (PendingRender pending in queued)
            {
                pending.Completion.TrySetCanceled();
                pending.Registration.Dispose();
            }
            if (_pump != null) StopCoroutine(_pump);
            Exception failure = null;
            try { (_capture as IDisposable)?.Dispose(); }
            catch (Exception exception) { failure = exception; Debug.LogException(exception, this); }
            if (_active != null)
            {
                if (failure != null) _active.Completion.TrySetException(failure);
                else _active.Completion.TrySetCanceled();
                _active.Registration.Dispose();
            }
            lock (_gate) _active = null;
            _capture = null;
            _pump = null;
            SetCaptureStatus("IDLE");
        }

        private sealed class PendingRender
        {
            public PendingRender(RenderRequest request, CancellationToken token, bool encodePng)
            { Request = request; Token = token; EncodePng = encodePng; }
            public bool EncodePng { get; }
            public bool IsActive;
            public CancellationTokenRegistration Registration;
            public ImageFrame Frame;
            public RenderRequest Request { get; }
            public CancellationToken Token { get; }
            public TaskCompletionSource<ImageFrame> Completion { get; } =
                new TaskCompletionSource<ImageFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
