using UnityEngine;

public sealed class MonsterView : MonoBehaviour
{
    public uint MonsterId { get; private set; }

    public void Initialize(uint monsterId, int x, int y)
    {
        MonsterId = monsterId;

        // 서버 좌표를 Unity 좌표로 변환해 초기 위치에 배치
        transform.position = WorldManager.ToUnityPosition(x, y);
    }
}
