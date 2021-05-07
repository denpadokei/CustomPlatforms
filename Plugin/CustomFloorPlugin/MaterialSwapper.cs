﻿using System.Linq;
using System.Threading.Tasks;

using UnityEngine;

using Zenject;


namespace CustomFloorPlugin
{
    /// <summary>
    /// The <see cref="MaterialSwapper"/>'s job is to swap the fake <see cref="Material"/>s on loaded resources after they are spawned against real ones<br/>
    /// Primary reason for this is the absence of proper custom <see cref="Shader"/>s (or decompiled source <see cref="Shader"/>s) and a lack of knowledge about their inner workings...<br/>
    /// Part of the documentation for this file is omitted because it's a clusterfuck and under construction.
    /// </summary>
    public class MaterialSwapper : IInitializable
    {
        internal readonly Task<(Material DarkEnvSimpleMaterial,
                                Material TransparentGlowMaterial,
                                Material OpaqueGlowMaterial)> MaterialsLoadingTask;

        private readonly TaskCompletionSource<(Material, Material, Material)> _taskSource;

        public MaterialSwapper()
        {
            _taskSource = new();
            MaterialsLoadingTask = _taskSource.Task;
        }

        public async void Initialize()
        {
            Material? darkEnvSimpleMaterial;
            Material? transparentGlowMaterial;
            Material? opaqueGlowMaterial;
            do
            {
                await Helpers.AsyncHelper.WaitForEndOfFrameAsync();
                Material[] materials = Resources.FindObjectsOfTypeAll<Material>();
                darkEnvSimpleMaterial = materials.FirstOrDefault(x => x.name == "DarkEnvironmentSimple");
                transparentGlowMaterial = materials.FirstOrDefault(x => x.name == "EnvLight");
                opaqueGlowMaterial = materials.FirstOrDefault(x => x.name == "EnvLightOpaque");
            } while (darkEnvSimpleMaterial == null || transparentGlowMaterial == null || opaqueGlowMaterial == null);
            opaqueGlowMaterial.DisableKeyword("ENABLE_HEIGHT_FOG");
            _taskSource.TrySetResult((darkEnvSimpleMaterial, transparentGlowMaterial, opaqueGlowMaterial));
        }

        /// <summary>
        /// Replaces all fake <see cref="Material"/>s on all <see cref="Renderer"/>s under a given <see cref="GameObject"/>
        /// </summary>
        /// <param name="gameObject"><see cref="GameObject"/> to search for <see cref="Renderer"/>s</param>
        internal async Task ReplaceMaterials(GameObject gameObject)
        {
            await MaterialsLoadingTask;
            foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                ReplaceForRenderer(renderer);
        }

        /// <summary>
        /// Replaces all fake <see cref="Material"/>s on a given <see cref="Renderer"/>
        /// </summary>
        /// <param name="renderer">What <see cref="Renderer"/> to replace materials for</param>
        private void ReplaceForRenderer(Renderer renderer)
        {
            Material[] materialsCopy = renderer.materials;
            bool materialsDidChange = false;
            for (int i = 0; i < materialsCopy.Length; i++)
            {
                switch (materialsCopy[i].name)
                {
                    case "_dark_replace (Instance)":
                        materialsCopy[i] = MaterialsLoadingTask.Result.DarkEnvSimpleMaterial;
                        materialsDidChange = true;
                        break;
                    case "_transparent_glow_replace (Instance)":
                        materialsCopy[i] = MaterialsLoadingTask.Result.TransparentGlowMaterial!;
                        materialsDidChange = true;
                        break;
                    case "_glow_replace (Instance)":
                        materialsCopy[i] = MaterialsLoadingTask.Result.OpaqueGlowMaterial!;
                        materialsDidChange = true;
                        break;
                }
            }
            if (materialsDidChange)
                renderer.sharedMaterials = materialsCopy;
        }
    }
}