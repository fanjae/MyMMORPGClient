using UnityEngine;
using UnityEngine.Rendering;

public sealed class PlayerView : MonoBehaviour
{
    public uint CharacterId { get; private set; }
    public const int RemoteSortingOrder = 10;
    public const int LocalSortingOrder = 20;

    public void SetLocalRendering(bool local)
    {
        // 프리팹의 자식 Sprite도 한 그룹으로 정렬해 겹친 로컬 Player가 앞에 보이게 한다.
        SortingGroup group = GetComponent<SortingGroup>();
        if (group == null)
            group = gameObject.AddComponent<SortingGroup>();
        group.sortingLayerID = SortingLayer.NameToID("Default");
        group.sortingOrder = local ? LocalSortingOrder : RemoteSortingOrder;
    }
    public int ServerX { get; private set; }
    public int ServerY { get; private set; }
    public double ExactServerX { get; private set; }
    public double ExactServerY { get; private set; }
    private ulong _lastTick;
    private bool _hasSnapshot;

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
        ExactServerX = x;
        ExactServerY = y;
        _targetPosition = WorldManager.ToUnityPosition(x, y);
        transform.position = _targetPosition;
    }

    public void SetTargetPosition(int x, int y)
    {
        ServerX = x;
        ServerY = y;
        ExactServerX = x;
        ExactServerY = y;
        PredictPosition(x, y);
    }

    public void PredictPosition(int x, int y)
    {
        // 화면 이동 예측은 서버 확정 좌표와 구분한다.
        _targetPosition = WorldManager.ToUnityPosition(x, y);
    }

    public void PredictPosition(double x, double y)
    {
        _targetPosition = WorldManager.ToUnityPosition(x, y);
    }

    public void ResetSnapshots()
    {
        _hasSnapshot = false;
        _lastTick = 0;
    }

    public void ApplySnapshot(MovementSnapshot state, bool local)
    {
        if (_hasSnapshot && (state.ServerTick < _lastTick || (state.ServerTick == _lastTick && state.Reason == MovementStateReason.Normal)))
            return;

        _hasSnapshot = true;
        _lastTick = state.ServerTick;
        ExactServerX = state.X;
        ExactServerY = state.Y;
        ServerX = (int)System.Math.Round(state.X, System.MidpointRounding.AwayFromZero);
        ServerY = (int)System.Math.Round(state.Y, System.MidpointRounding.AwayFromZero);
        if (!local || state.Reason != MovementStateReason.Normal)
            PredictPosition(state.X, state.Y);

        if (state.Reason == MovementStateReason.Respawned || (transform.position - WorldManager.ToUnityPosition(state.X, state.Y)).sqrMagnitude > 4f)
            transform.position = WorldManager.ToUnityPosition(state.X, state.Y);
    }
}
