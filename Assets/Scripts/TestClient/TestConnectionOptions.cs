using System;
using System.Globalization;

public static class TestConnectionOptions
{
    public static ushort GamePort(ushort serverPort, string[] arguments = null)
    {
        arguments ??= Environment.GetCommandLineArgs();
        const string prefix = "--test-game-port=";
        ushort port = serverPort;
        bool found = false;
        foreach (string argument in arguments)
        {
            if (!argument.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            // 지연 프록시 검증에만 명시한 포트를 사용하고 기본 서버 안내 포트는 유지한다.
            if (found || !ushort.TryParse(argument.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port == 0)
                throw new ArgumentException("--test-game-port requires one port between 1 and 65535.");
            found = true;
        }
        return port;
    }
}
