using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

internal static class Program
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, string> History = new();
    private static readonly Dictionary<string, PeerState> Peers = new();
    private static readonly Stopwatch Clock = new();

    private static UdpClient udp = null!;
    private static IPEndPoint remote = null!;
    private static string peerId = "";

    private static int ownX;
    private static int ownY;
    private static long ownSequence;

    private static long sentCount;
    private static long sendErrorCount;
    private static long receivedCount;
    private static long appliedCount;
    private static long ignoredCount;
    private static long invalidCount;

    private static volatile bool stopping;

    private sealed class PeerState
    {
        public long LastSequence;
        public int X;
        public int Y;
        public HashSet<long> Seen = new();
    }

    private static async Task Main(string[] args)
    {
        if (args.Length != 5
            || !IPAddress.TryParse(args[0], out IPAddress? localAddress)
            || !TryPort(args[1], out int localPort)
            || !IPAddress.TryParse(args[2], out IPAddress? remoteAddress)
            || !TryPort(args[3], out int remotePort)
            || !ValidId(args[4]))
        {
            Console.WriteLine(
                "사용법: dotnet run -- <내 IP> <내 포트> <상대 IP> <상대 포트> <내 ID>");
            return;
        }

        peerId = args[4];
        remote = new IPEndPoint(remoteAddress, remotePort);
        IPEndPoint local = new IPEndPoint(localAddress, localPort);

        if (local.Equals(remote))
        {
            Console.WriteLine("내 주소와 상대 주소는 달라야 합니다.");
            return;
        }

        try
        {
            udp = new UdpClient(local);
        }
        catch (SocketException error)
        {
            Console.WriteLine("포트 열기 실패: " + error.Message);
            return;
        }

        Clock.Start();

        Console.WriteLine($"내 ID: {peerId}");
        Console.WriteLine($"내 주소: {local}");
        Console.WriteLine($"상대 주소: {remote}");

        PrintHelp();
        PrintStatus();

        Task receiveTask = ReceiveLoopAsync();

        try
        {
            await Task.Run(InputLoopAsync);
        }
        finally
        {
            stopping = true;
            udp.Close();
            await receiveTask;
            Clock.Stop();
            PrintStatus();
            udp.Dispose();
        }
    }

    private static bool TryPort(string text, out int port)
    {
        return int.TryParse(text, out port)
            && port >= 1 && port <= 65535;
    }

    private static bool ValidId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32)
            return false;

        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || character == '|')
                return false;
        }

        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("좌표 입력: 3 5");
        Console.WriteLine("/status : 상태와 통계");
        Console.WriteLine("/replay 1 : 저장된 1번 메시지 재전송");
        Console.WriteLine("/bad empty : 빈 메시지 전송");
        Console.WriteLine("/bad format : 잘못된 형식 전송");
        Console.WriteLine("/bad value : 잘못된 숫자 전송");
        Console.WriteLine("/load 100 100 : 100개, 전송 간격 100ms");
        Console.WriteLine("/help : 명령 안내");
        Console.WriteLine("/exit : 종료");
    }

    private static async Task InputLoopAsync()
    {
        while (true)
        {
            string? line = Console.ReadLine();

            if (line == null)
                return;

            line = line.Trim();

            if (line == "/exit")
                return;

            if (line == "/help")
            {
                PrintHelp();
                continue;
            }

            if (line == "/status")
            {
                PrintStatus();
                continue;
            }

            string[] parts = line.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 && parts[0] == "/replay")
            {
                if (!long.TryParse(parts[1], out long sequence))
                {
                    Console.WriteLine("순서 번호를 정수로 입력하세요.");
                    continue;
                }

                string? saved;

                lock (Gate)
                {
                    History.TryGetValue(sequence, out saved);
                }

                if (saved == null)
                {
                    Console.WriteLine("저장된 메시지가 없습니다.");
                    continue;
                }

                await SendAsync(saved);
                Console.WriteLine("예전 메시지 재전송: " + saved);
                continue;
            }

            if (parts.Length == 2 && parts[0] == "/bad")
            {
                string? message = parts[1] switch
                {
                    "empty" => "",
                    "format" => "INVALID",
                    "value" => $"POSITION|{peerId}|abc|3|5",
                    _ => null
                };

                if (message == null)
                {
                    Console.WriteLine("empty, format, value 중 선택하세요.");
                    continue;
                }

                await SendAsync(message);
                Console.WriteLine("잘못된 메시지 시험 전송: " + parts[1]);
                continue;
            }

            if (parts.Length == 3 && parts[0] == "/load")
            {
                if (!int.TryParse(parts[1], out int count)
                    || !int.TryParse(parts[2], out int interval)
                    || count < 1 || count > 10000
                    || interval < 0 || interval > 1000)
                {
                    Console.WriteLine(
                        "개수는 1~10000, 간격은 0~1000ms로 입력하세요.");
                    continue;
                }

                await RunLoadAsync(count, interval);
                continue;
            }

            if (parts.Length == 2
                && int.TryParse(parts[0], out int x)
                && int.TryParse(parts[1], out int y))
            {
                bool unchanged;

                lock (Gate)
                {
                    unchanged = ownSequence > 0 && ownX == x && ownY == y;
                }

                if (unchanged)
                {
                    Console.WriteLine("같은 위치입니다. 다른 좌표를 입력하세요.");
                    continue;
                }

                await SendPositionAsync(x, y);
                PrintStatus();
                continue;
            }

            Console.WriteLine("입력 오류: 좌표는 3 5 형식으로 입력하세요.");
        }
    }

    private static async Task SendPositionAsync(int x, int y)
    {
        string message;

        lock (Gate)
        {
            ownX = x;
            ownY = y;
            ownSequence++;

            message = $"POSITION|{peerId}|{ownSequence}|{ownX}|{ownY}";
            History[ownSequence] = message;
        }

        await SendAsync(message);
    }

    private static async Task SendAsync(string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);

        try
        {
            await udp.SendAsync(bytes, bytes.Length, remote);

            lock (Gate)
            {
                sentCount++;
            }
        }
        catch (SocketException error)
        {
            lock (Gate)
            {
                sendErrorCount++;
                Console.WriteLine("송신 오류: " + error.Message);
            }
        }
    }

    private static async Task RunLoadAsync(int count, int interval)
    {
        long beforeSent;
        long beforeErrors;
        int startX;
        int startY;

        lock (Gate)
        {
            beforeSent = sentCount;
            beforeErrors = sendErrorCount;
            startX = ownX;
            startY = ownY;
        }

        if ((long)startX + count > int.MaxValue)
        {
            Console.WriteLine("X 좌표가 정수 범위를 초과합니다.");
            return;
        }

        Stopwatch measurement = Stopwatch.StartNew();

        for (int i = 1; i <= count; i++)
        {
            await SendPositionAsync(startX + i, startY);

            if (interval > 0 && i < count)
                await Task.Delay(interval);
        }

        measurement.Stop();

        lock (Gate)
        {
            Console.WriteLine(
                $"전송량 시험 완료: 요청 {count}개, 간격 {interval}ms");

            Console.WriteLine(
                $"송신 성공 {sentCount - beforeSent}, " +
                $"송신 오류 {sendErrorCount - beforeErrors}, " +
                $"송신 소요 {measurement.Elapsed.TotalSeconds:F3}초");
        }

        PrintStatus();
    }

    private static async Task ReceiveLoopAsync()
    {
        while (!stopping)
        {
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync();
                ProcessReceived(result);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException error)
            {
                if (stopping)
                    return;

                lock (Gate)
                {
                    Console.WriteLine("수신 소켓 오류: " + error.Message);
                }
            }
        }
    }

    private static void ProcessReceived(UdpReceiveResult result)
    {
        lock (Gate)
        {
            receivedCount++;

            if (!result.RemoteEndPoint.Equals(remote))
            {
                Reject("설정한 상대 주소와 다릅니다.");
                return;
            }

            if (result.Buffer.Length == 0 || result.Buffer.Length > 1024)
            {
                Reject("빈 메시지 또는 크기 초과");
                return;
            }

            string message = Encoding.UTF8.GetString(result.Buffer);
            string[] parts = message.Split('|');

            if (parts.Length != 5
                || parts[0] != "POSITION"
                || !ValidId(parts[1])
                || parts[1] == peerId
                || !long.TryParse(parts[2], out long sequence)
                || sequence <= 0
                || !int.TryParse(parts[3], out int x)
                || !int.TryParse(parts[4], out int y))
            {
                Reject("형식, 피어 ID 또는 숫자가 잘못되었습니다.");
                return;
            }

            string senderId = parts[1];

            if (!Peers.TryGetValue(senderId, out PeerState? state))
            {
                state = new PeerState();
                Peers[senderId] = state;
            }

            state.Seen.Add(sequence);

            if (sequence <= state.LastSequence)
            {
                ignoredCount++;

                Console.WriteLine(
                    $"무시: {senderId} 번호 {sequence}, " +
                    $"현재 번호 {state.LastSequence}, " +
                    $"유지 위치 ({state.X}, {state.Y})");
                return;
            }

            long previousSequence = state.LastSequence;

            state.LastSequence = sequence;
            state.X = x;
            state.Y = y;
            appliedCount++;

            Console.WriteLine(
                $"반영: {senderId}, 번호 {sequence}, 위치 ({x}, {y})");

            if (sequence > previousSequence + 1)
            {
                Console.WriteLine(
                    "번호 건너뜀 관찰: 늦은 도착 또는 누락 가능성");
            }
        }
    }

    private static void Reject(string reason)
    {
        invalidCount++;
        Console.WriteLine("메시지 거절: " + reason);
    }

    private static void PrintStatus()
    {
        lock (Gate)
        {
            Console.WriteLine(
                $"내 상태: {peerId}, 위치 ({ownX}, {ownY}), 번호 {ownSequence}");

            if (Peers.Count == 0)
            {
                Console.WriteLine("상대 상태: 미수신");
            }

            foreach (KeyValuePair<string, PeerState> entry in Peers)
            {
                PeerState state = entry.Value;
                long notObserved = state.LastSequence - state.Seen.Count;

                Console.WriteLine(
                    $"상대 상태: {entry.Key}, " +
                    $"위치 ({state.X}, {state.Y}), " +
                    $"번호 {state.LastSequence}");

                Console.WriteLine(
                    $"1~{state.LastSequence} 중 미관찰 번호: {notObserved}");
            }

            Console.WriteLine(
                $"송신 성공 {sentCount}, 송신 오류 {sendErrorCount}");

            Console.WriteLine(
                $"수신 {receivedCount}, 반영 {appliedCount}, " +
                $"오래된/중복 무시 {ignoredCount}, 잘못된 메시지 {invalidCount}");

            Console.WriteLine(
                $"실행 경과: {Clock.Elapsed.TotalSeconds:F3}초");
        }
    }
}