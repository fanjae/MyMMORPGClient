using UnityEngine;

public sealed class PlayerView : MonoBehaviour
{
    public uint CharacterId { get; private set; }

    public void Initialize(uint characterId, int x, int y)
    {
        CharacterId = characterId;
        SetPosition(x, y);
    }

    public void SetPosition(int x, int y)
    {
        // 서버 좌표를 Unity 월드 좌표로 변환
        transform.position = WorldManager.ToUnityPosition(x, y);
    }
}
