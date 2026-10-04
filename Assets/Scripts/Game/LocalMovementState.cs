public sealed class LocalMovementState
{
    public uint MapId { get; private set; }
    public int ServerX { get; private set; }
    public int ServerY { get; private set; }
    public ulong PendingSequence { get; private set; }
    public bool HasPendingMove => PendingSequence != 0;

    private ulong _sequence;

    public void Reset()
    {
        _sequence = 0;
        EnterMap(0, 0, 0);
    }

    public void EnterMap(uint mapId, int x, int y)
    {
        MapId = mapId;
        ServerX = x;
        ServerY = y;
        PendingSequence = 0;
    }

    public bool TryBeginMove(int x, int y, out MoveRequestData request)
    {
        request = default;
        if (MapId == 0 || HasPendingMove || _sequence == ulong.MaxValue)
            return false;

        PendingSequence = ++_sequence;
        request = new MoveRequestData { MapId = MapId, Sequence = PendingSequence, X = x, Y = y };
        return true;
    }

    public bool TryApplyResponse(MoveResponseData data)
    {
        // 맵 전환이나 새 요청 이후 도착한 이전 응답이 현재 위치를 덮지 않도록 한다.
        if (!HasPendingMove || data.Sequence != PendingSequence || data.MapId != MapId)
            return false;

        PendingSequence = 0;
        ServerX = data.X;
        ServerY = data.Y;
        return true;
    }

    public bool CancelMove(ulong sequence)
    {
        if (!HasPendingMove || PendingSequence != sequence)
            return false;

        PendingSequence = 0;
        return true;
    }
}
