using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumForUnity;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DefaultExecutionOrder(-1000)]
    public sealed class CesiumIonEnvironmentLoader : MonoBehaviour
    {
        private const string TokenVariable = "CESIUM_ION_ACCESS_TOKEN";
        private const string AssetVariable = "CESIUM_ION_ASSET_ID";

        [SerializeField] private Cesium3DTileset[] ionTilesets;
        [SerializeField] private long defaultIonAssetId = 1;

        private void Awake()
        {
            ApplyCredentials();
        }

        public void Configure(params Cesium3DTileset[] tilesets)
        {
            ionTilesets = tilesets;
        }

        [ContextMenu("Apply Cesium ion Credentials")]
        public void ApplyCredentials()
        {
            IReadOnlyDictionary<string, string> fileValues = ReadDotEnv();
            string token = ReadSetting(TokenVariable, fileValues);
            if (string.IsNullOrWhiteSpace(token))
            {
                SetIonTilesetsEnabled(false);
                Debug.LogWarning(
                    $"Cesium ion terrain is disabled. Set {TokenVariable} in the project-root .env " +
                    "or in the process environment, then enter Play Mode again.",
                    this);
                return;
            }

            long assetId = defaultIonAssetId;
            string assetValue = ReadSetting(AssetVariable, fileValues);
            if (!string.IsNullOrWhiteSpace(assetValue) &&
                !long.TryParse(assetValue, NumberStyles.None, CultureInfo.InvariantCulture, out assetId))
            {
                Debug.LogError($"{AssetVariable} must be an integer Cesium ion asset ID.", this);
                SetIonTilesetsEnabled(false);
                return;
            }

            foreach (Cesium3DTileset tileset in ionTilesets ?? Array.Empty<Cesium3DTileset>())
            {
                if (tileset == null)
                {
                    continue;
                }

                tileset.tilesetSource = CesiumDataSource.FromCesiumIon;
                tileset.ionAssetID = assetId;
                tileset.ionAccessToken = token;
                tileset.enabled = true;
            }
        }

        private void SetIonTilesetsEnabled(bool enabled)
        {
            foreach (Cesium3DTileset tileset in ionTilesets ?? Array.Empty<Cesium3DTileset>())
            {
                if (tileset != null)
                {
                    tileset.enabled = enabled;
                }
            }
        }

        private static string ReadSetting(
            string name,
            IReadOnlyDictionary<string, string> fileValues)
        {
            string processValue = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(processValue))
            {
                return processValue.Trim();
            }

            return fileValues.TryGetValue(name, out string fileValue) ? fileValue : string.Empty;
        }

        private static IReadOnlyDictionary<string, string> ReadDotEnv()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(projectRoot, ".env");
            if (!File.Exists(path))
            {
                return values;
            }

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim().Trim('"', '\'');
                values[key] = value;
            }

            return values;
        }
    }
}
