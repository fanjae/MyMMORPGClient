public enum LoginPacketOpcode : ushort
{
    LoginRequest = 1,
    LoginResponse = 2,
    CharacterListRequest = 3,
    CharacterListResponse = 4,
    CharacterSelectRequest = 5,
    CharacterSelectResponse = 6
}

public enum GamePacketOpcode : ushort
{
    EnterGameRequest = 1,
    EnterGameResponse = 2,
    PlayerEnterMap = 3,
    PlayerLeaveMap = 4,
    MoveRequest = 5,
    PlayerMove = 6,
    ChangeMapRequest = 7,
    ChangeMapResponse = 8,
    ChatRequest = 9,
    PlayerChat = 10,
    MonsterEnterMap = 11,
    MoveResponse = 12,
    MapInfo = 13
}
