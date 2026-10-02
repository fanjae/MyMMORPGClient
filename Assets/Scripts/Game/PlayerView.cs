using UnityEngine;

public sealed class PlayerView : MonoBehaviour
{
    public uint CharacterId { get; private set; }
    public int ServerX { get; private set; }
    public int ServerY { get; private set; }

    private Vector3 _targetPosition;
    private const float InterpolationSpeed = 12f;

    private void Update()
    {
        // 수신 좌표 사이를 짧게 보간하고 목표에 가까워지면 정확한 위치로 맞춘다.
        float blend = 1f - Mathf.Exp(-InterpolationSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, _targetPosition, blend);

        if ((transform.position - _targetPosition).sqrMagnitude < 0.0001f)
            transform.position = _targetPosition;
    }

    public void Initialize(uint characterId, int x, int y)
    {
        CharacterId = characterId;
        SetPosition(x, y);
    }

    public void SetPosition(int x, int y)
    {
        // 서버 좌표를 Unity 월드 좌표로 변환
        ServerX = x;
        ServerY = y;
        _targetPosition = WorldManager.ToUnityPosition(x, y);
        transform.position = _targetPosition;
    }

    public void SetTargetPosition(int x, int y)
    {
        ServerX = x;
        ServerY = y;
        _targetPosition = WorldManager.ToUnityPosition(x, y);
    }
}
