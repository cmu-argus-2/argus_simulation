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
        [SerializeField, Min(1f)] private float maximumLoadSeconds = 240f;

        private readonly object _gate = new object();
        private readonly Queue<PendingRender> _requests = new Queue<PendingRender>();
        private bool _accepting;
        private PendingRender _active;
        private IEnumerator _capture;
        private Coroutine _pump;

        public string RendererName => "Unity/Cesium";
        public string CaptureStatus { get; private set; } = "IDLE";
        public event Action<string> CaptureStatusChanged;

        private void SetCaptureStatus(string status)
        {
            if (CaptureStatus == status) return;
            CaptureStatus = status;
            CaptureStatusChanged?.Invoke(status);
        }
        public bool IsRendering { get { lock (_gate) return _active != null || _requests.Count > 0; } }

        public void Configure(CubeSatCameraRig rig, CesiumGeoreference reference,
            Light source = null, NasaGibsRasterController raster = null)
        {
            cameraRig = rig;
            georeference = reference;
            sunlight = source;
            rasterController = raster;
        }

        public Task<ImageFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            if (!request.IsValid)
                return Task.FromException<ImageFrame>(new ArgumentException("A valid render request is required."));
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<ImageFrame>(cancellationToken);

            var pending = new PendingRender(request, cancellationToken);
            lock (_gate)
            {
                if (!_accepting)
                    return Task.FromException<ImageFrame>(new InvalidOperationException("Renderer is disabled."));
                _requests.Enqueue(pending);
            }
            return pending.Completion.Task;
        }

        private void OnEnable() { lock (_gate) _accepting = true; }

        private void Update()
        {
            lock (_gate)
            {
                if (_active != null || _requests.Count == 0) return;
                _active = _requests.Dequeue();
            }
            _capture = Capture(_active);
            _pump = StartCoroutine(Pump());
        }

        // Catch coroutine exceptions and deliver them through RenderAsync, including cleanup errors.
        private IEnumerator Pump()
        {
            while (true)
            {
                bool next;
                try { next = _capture.MoveNext(); }
                catch (OperationCanceledException) { _active.Completion.TrySetCanceled(); break; }
                catch (Exception exception) { _active.Completion.TrySetException(exception); break; }
                if (!next) break;
                yield return _capture.Current;
            }
            (_capture as IDisposable)?.Dispose();
            _capture = null;
            lock (_gate) _active = null;
            _pump = null;
            SetCaptureStatus("IDLE");
        }

        private IEnumerator Capture(PendingRender pending)
        {
            pending.Token.ThrowIfCancellationRequested();
            ResolveScene();
            RenderRequest request = pending.Request;
            Camera camera = cameraRig.RequestCamera;
            RenderTexture target = null;
            CesiumCameraManager manager = CesiumCameraManager.GetOrCreate(cameraRig.gameObject);
            var previousViews = new List<Camera>(manager.additionalCameras);
            bool previousMainView = manager.useMainCamera;
            bool previousSceneView = manager.useSceneViewCameraInEditor;
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
                // Loading progress is tileset-wide. Select only the exposure view while waiting,
                // so unrelated dashboard and look-ahead views cannot keep it below 100%.
                SelectCaptureView(manager, camera, previousViews, ref previousMainView, ref previousSceneView);
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
                rasterController?.SetCaptureSunDirection(towardSun);

                float startedAt = Time.realtimeSinceStartup;
                float lastProgressAt = startedAt;
                float lastProgress = -1f;
                int stable = 0;
                while (stable < Math.Max(1, stableFramesRequired))
                {
                    pending.Token.ThrowIfCancellationRequested();
                    // Reapply after origin shifts; poses are Earth-fixed, not stale Unity positions.
                    ApplyPose(camera, request);
                    SelectCaptureView(manager, camera, previousViews, ref previousMainView, ref previousSceneView);
                    float progress = MinimumEnabledTilesetProgress();
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
                    yield return null;
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
                    byte[] pixels = image.GetRawTextureData<byte>().ToArray();
                    FlipRows(pixels, request.Intrinsics.WidthPixels * 3, request.Intrinsics.HeightPixels);
                    pending.Completion.TrySetResult(new ImageFrame(request, ImageEncoding.Rgb8, pixels, RendererName));
                }
                finally
                {
                    if (rebase)
                    {
                        if (originalAuthority == CesiumGeoreferenceOriginAuthority.LongitudeLatitudeHeight)
                            georeference.SetOriginLongitudeLatitudeHeight(originalLlh.x, originalLlh.y, originalLlh.z);
                        else
                            georeference.SetOriginEarthCenteredEarthFixed(originalOrigin.x, originalOrigin.y, originalOrigin.z);
                        georeference.originPlacement = originalPlacement;
                        ApplyPose(camera, request);
                    }
                    if (sunlight != null) sunlight.transform.rotation = originalLightRotation;
                    if (image != null) Destroy(image);
                }
            }
            finally
            {
                rasterController?.SetCaptureSunDirection(null);
                if (registered && manager != null)
                {
                    manager.additionalCameras.Remove(camera);
                    foreach (Camera view in previousViews)
                        if (view != null && !manager.additionalCameras.Contains(view)) manager.additionalCameras.Add(view);
                    manager.useMainCamera = previousMainView;
                    manager.useSceneViewCameraInEditor = previousSceneView;
                }
                if (camera != null) { camera.targetTexture = null; camera.ResetProjectionMatrix(); }
                if (target != null) { target.Release(); Destroy(target); }
            }
        }

        private static void SelectCaptureView(CesiumCameraManager manager, Camera camera,
            List<Camera> previousViews, ref bool previousMainView, ref bool previousSceneView)
        {
            // Warm-up can finish while a request is loading. Remember views it restores too.
            foreach (Camera view in manager.additionalCameras)
                if (view != null && view != camera && !previousViews.Contains(view)) previousViews.Add(view);
            previousMainView |= manager.useMainCamera;
            previousSceneView |= manager.useSceneViewCameraInEditor;
            manager.useMainCamera = false;
            manager.useSceneViewCameraInEditor = false;
            manager.additionalCameras.Clear();
            manager.additionalCameras.Add(camera);
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

        internal static float MinimumEnabledTilesetProgress()
        {
            float progress = 100f;
            foreach (Cesium3DTileset tileset in FindObjectsByType<Cesium3DTileset>(FindObjectsInactive.Exclude))
            {
                if (!tileset.isActiveAndEnabled) continue;
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
            lock (_gate)
            {
                _accepting = false;
                while (_requests.Count > 0) _requests.Dequeue().Completion.TrySetCanceled();
            }
            if (_pump != null) StopCoroutine(_pump);
            (_capture as IDisposable)?.Dispose();
            _active?.Completion.TrySetCanceled();
            lock (_gate) _active = null;
            _capture = null;
            _pump = null;
            SetCaptureStatus("IDLE");
        }

        private sealed class PendingRender
        {
            public PendingRender(RenderRequest request, CancellationToken token) { Request = request; Token = token; }
            public RenderRequest Request { get; }
            public CancellationToken Token { get; }
            public TaskCompletionSource<ImageFrame> Completion { get; } =
                new TaskCompletionSource<ImageFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
