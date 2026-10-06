using System;
using System.Net.Sockets;

public static class ConnectionError
{
    public static string Describe(Exception exception, string host, int port, string server)
    {
        // 로그인 실패 응답과 TCP 연결 실패를 구분하고 실제 접속 대상을 함께 표시한다.
        string endpoint = $"{server} {host}:{port}";
        Exception cause = exception.GetBaseException();
        if (cause is SocketException socket)
        {
            switch (socket.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return $"{endpoint} 연결 거부 ({socket.ErrorCode}). 서버 실행, 수신 주소와 방화벽/포트 전달을 확인하세요.";
                case SocketError.TimedOut:
                    return $"{endpoint} 연결 시간 초과 ({socket.ErrorCode}). 대상 주소와 네트워크 경로를 확인하세요.";
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return $"{endpoint} 주소를 찾을 수 없습니다. Host를 확인하세요.";
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                    return $"{endpoint}에 도달할 수 없습니다 ({socket.ErrorCode}). 네트워크 경로를 확인하세요.";
            }
        }
        if (cause is TimeoutException)
            return $"{endpoint} 연결 시간이 초과되었습니다.";
        return $"{endpoint} 연결 실패: {exception.Message}";
    }
}
