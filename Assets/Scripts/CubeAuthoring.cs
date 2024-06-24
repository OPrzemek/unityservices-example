using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

public struct Cube : IComponentData
{
}

[MaterialProperty("_Color")]
public struct CubeColor : IComponentData
{
    public float4 Value;
}

[DisallowMultipleComponent]
public class CubeAuthoring : MonoBehaviour
{
    class Baker : Baker<CubeAuthoring>
    {
        public override void Bake(CubeAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<Cube>(entity);
        }
    }
}