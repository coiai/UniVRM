﻿using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.Threading.Tasks;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UniGLTF
{
    [Flags]
    public enum TextureLoadFlags
    {
        None = 0,
        Used = 1,
        External = 1 << 1,
    }

    public struct TextureLoadInfo
    {
        public readonly Texture2D Texture;
        public readonly TextureLoadFlags Flags;
        public bool IsUsed => Flags.HasFlag(TextureLoadFlags.Used);
        public bool IsExternal => Flags.HasFlag(TextureLoadFlags.External);

        public TextureLoadInfo(Texture2D texture, bool used, bool isExternal)
        {
            Texture = texture;
            var flags = TextureLoadFlags.None;
            if (used)
            {
                flags |= TextureLoadFlags.Used;
            }
            if (isExternal)
            {
                flags |= TextureLoadFlags.External;
            }
            Flags = flags;
        }
    }

    public delegate Task<TextureLoadInfo> LoadTextureAsyncFunc(IAwaitCaller awaitCaller, int index, bool used);
    public delegate Task<Texture2D> GetTextureAsyncFunc(IAwaitCaller awaitCaller, glTF gltf, GetTextureParam param);
    public class TextureFactory : IDisposable
    {
        Dictionary<string, Texture2D> m_externalMap;
        public bool TryGetExternal(GetTextureParam param, bool used, out Texture2D external)
        {
            if (param.Index0.HasValue && m_externalMap != null)
            {
                if (m_externalMap.TryGetValue(param.Name, out external))
                {
                    // Debug.Log($"use external: {param.Name}");
                    m_textureCache.Add(param.Name, new TextureLoadInfo(external, used, true));
                    return external;
                }
            }
            external = default;
            return false;
        }

        public UnityPath ImageBaseDir { get; set; }

        public TextureFactory(LoadTextureAsyncFunc loadTextureAsync,
            IEnumerable<(string, UnityEngine.Object)> externalMap)
        {
            LoadTextureAsync = loadTextureAsync;
            if (externalMap != null)
            {
                m_externalMap = externalMap
                    .Select(kv => (kv.Item1, kv.Item2 as Texture2D))
                    .Where(kv => kv.Item2 != null)
                    .ToDictionary(kv => kv.Item1, kv => kv.Item2);
            }
        }

        public void Dispose()
        {
            foreach (var x in ObjectsForSubAsset())
            {
                UnityEngine.Object.DestroyImmediate(x, true);
            }
        }

        public IEnumerable<UnityEngine.Object> ObjectsForSubAsset()
        {
            foreach (var kv in m_textureCache)
            {
                yield return kv.Value.Texture;
            }
        }

        Dictionary<string, TextureLoadInfo> m_textureCache = new Dictionary<string, TextureLoadInfo>();

        public IEnumerable<TextureLoadInfo> Textures => m_textureCache.Values;

        public LoadTextureAsyncFunc LoadTextureAsync;

        async Task<TextureLoadInfo> GetOrCreateBaseTexture(IAwaitCaller awaitCaller, glTF gltf, int textureIndex, bool used)
        {
            var name = gltf.textures[textureIndex].name;
            if (!m_textureCache.TryGetValue(name, out TextureLoadInfo cacheInfo))
            {
                cacheInfo = await LoadTextureAsync(awaitCaller, textureIndex, used);
                m_textureCache.Add(name, cacheInfo);
            }
            return cacheInfo;
        }

        /// <summary>
        /// テクスチャーをロード、必要であれば変換して返す。
        /// 同じものはキャッシュを返す
        /// </summary>
        /// <param name="texture_type">変換の有無を判断する: METALLIC_GLOSS_PROP</param>
        /// <param name="roughnessFactor">METALLIC_GLOSS_PROPの追加パラメーター</param>
        /// <param name="indices">gltf の texture index</param>
        /// <returns></returns>
        public async Task<Texture2D> GetTextureAsync(IAwaitCaller awaitCaller, glTF gltf, GetTextureParam param)
        {
            if (m_textureCache.TryGetValue(param.Name, out TextureLoadInfo cacheInfo))
            {
                return cacheInfo.Texture;
            }
            if (TryGetExternal(param, true, out Texture2D external))
            {
                return external;
            }

            switch (param.TextureType)
            {
                case GetTextureParam.NORMAL_PROP:
                    {
                        if (Application.isPlaying)
                        {
                            var baseTexture = await GetOrCreateBaseTexture(awaitCaller, gltf, param.Index0.Value, false);
                            var converted = new NormalConverter().GetImportTexture(baseTexture.Texture);
                            var info = new TextureLoadInfo(converted, true, false);
                            m_textureCache.Add(param.Name, info);
                            return info.Texture;
                        }
                        else
                        {
#if UNITY_EDITOR
                            var info = await LoadTextureAsync(awaitCaller, param.Index0.Value, true);
                            var name = gltf.textures[param.Index0.Value].name;
                            m_textureCache.Add(name, info);

                            var textureAssetPath = AssetDatabase.GetAssetPath(info.Texture);
                            TextureIO.MarkTextureAssetAsNormalMap(textureAssetPath);
#endif
                            return info.Texture;
                        }
                    }

                case GetTextureParam.METALLIC_GLOSS_PROP:
                    {
                        // Bake roughnessFactor values into a texture.
                        var baseTexture = await GetOrCreateBaseTexture(awaitCaller, gltf, param.Index0.Value, false);
                        var converted = new MetallicRoughnessConverter(param.MetallicFactor).GetImportTexture(baseTexture.Texture);
                        converted.name = param.Name;
                        var info = new TextureLoadInfo(converted, true, false);
                        m_textureCache.Add(param.Name, info);
                        return info.Texture;
                    }

                case GetTextureParam.OCCLUSION_PROP:
                    {
                        var baseTexture = await GetOrCreateBaseTexture(awaitCaller, gltf, param.Index0.Value, false);
                        var converted = new OcclusionConverter().GetImportTexture(baseTexture.Texture);
                        converted.name = param.Name;
                        var info = new TextureLoadInfo(converted, true, false);
                        m_textureCache.Add(param.Name, info);
                        return info.Texture;
                    }

                default:
                    {
                        var baseTexture = await GetOrCreateBaseTexture(awaitCaller, gltf, param.Index0.Value, true);
                        return baseTexture.Texture;
                    }

                    throw new NotImplementedException();
            }
        }
    }
}
