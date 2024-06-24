using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using Unity.Burst;
using Unity.Collections;
using Unity.Rendering;

[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[BurstCompile]
public partial struct CubeMovementSystem : ISystem
{

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var speed = SystemAPI.Time.DeltaTime * 4;
        foreach (var (input, trans) in SystemAPI.Query<RefRO<CubeInput>, RefRW<LocalTransform>>().WithAll<Simulate>())
        {
            var moveInput = new float2(input.ValueRO.Horizontal, input.ValueRO.Vertical);
            moveInput = math.normalizesafe(moveInput) * speed;
            trans.ValueRW.Position += new float3(moveInput.x, 0, moveInput.y);
        }
    }
}

[BurstCompile]
public partial struct PlayerColorSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        //state.Enabled = false;

        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
        foreach (var (baseColor, ghostOwner) in SystemAPI.Query<RefRW<URPMaterialPropertyBaseColor>, RefRO<GhostOwner>>())
        {
            if (ghostOwner.ValueRO.NetworkId == 1)
                baseColor.ValueRW.Value = new float4(1f, 0f, 0f, 1f);
            else baseColor.ValueRW.Value = new float4(0f, 0f, 1f, 1f);
        }
        commandBuffer.Playback(state.EntityManager);
    }
}