// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;
using System.Text;

namespace OpenShell.IntegrationTests;

/// <summary>
/// Exercises the sandbox features that only a real gateway and supervisor can prove: exec, file transfer,
/// watch and logs, lifecycle and updates, TCP forwarding, and SSH. Each test owns its sandbox.
/// </summary>
public class SandboxFeatureTests
{
    // perl-base is essential in Ubuntu, so this echo server works in the default sandbox image.
    private const string EchoServer =
        "perl -MIO::Socket::INET -e '$s=IO::Socket::INET->new(LocalAddr=>\"127.0.0.1\",LocalPort=>shift,Listen=>5,ReuseAddr=>1) or die $!;" +
        " while($c=$s->accept){while(defined($l=<$c>)){print $c $l} close $c}'";

    [Test]
    [Timeout(600_000)]
    public async Task Exec_passes_stdin_environment_and_working_directory(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-exec", cancellationToken);

        var result = await client.Exec.RunAsync(
            sandbox.Name,
            ["/bin/sh", "-c", "pwd; echo \"$GREETING\"; cat"],
            new ExecOptions
            {
                WorkingDirectory = "/tmp",
                Environment = new Dictionary<string, string> { ["GREETING"] = "hello from dotnet" },
                Stdin = Encoding.UTF8.GetBytes("piped input"),
            },
            cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.StdoutText.Split('\n')).IsEquivalentTo(new[] { "/tmp", "hello from dotnet", "piped input" });
    }

    [Test]
    [Timeout(600_000)]
    public async Task Interactive_exec_reads_stdin_until_eof(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-stdin", cancellationToken);

        await using var session = await client.Exec.InteractiveAsync(
            sandbox.Name, ["/bin/sh", "-c", "read line; echo \"got $line\"; exit 7"], 80, 24, allocateTty: false, cancellationToken: cancellationToken);
        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("ping\n"), cancellationToken);
        await session.CompleteStdinAsync();

        var stdout = new StringBuilder();
        ExecExit? exit = null;
        await foreach (var e in session.ReadEventsAsync(cancellationToken))
        {
            switch (e)
            {
                case ExecOutput { IsStdErr: false } output:
                    stdout.Append(output.Text);
                    break;
                case ExecExit x:
                    exit = x;
                    break;
            }
        }

        await Assert.That(stdout.ToString().Trim()).IsEqualTo("got ping");
        await Assert.That(exit).IsEqualTo(new ExecExit(7));
    }

    [Test]
    [Timeout(600_000)]
    public async Task Files_round_trip_through_upload_and_download(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-files", cancellationToken);
        var local = Directory.CreateTempSubdirectory("openshell-it-");
        try
        {
            // A single file from a stream, with a mode and a parent directory that does not exist yet.
            var payload = new byte[256 * 1024];
            Random.Shared.NextBytes(payload);
            using (var upload = new MemoryStream(payload))
            {
                await client.Files.UploadAsync(sandbox.Name, upload, "/tmp/dotnet-it/blob.bin", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, cancellationToken);
            }

            await Assert.That((await sandbox.ShAsync("stat -c '%s %a' /tmp/dotnet-it/blob.bin", cancellationToken)).Trim()).IsEqualTo($"{payload.Length} 700");

            using var download = new MemoryStream();
            await client.Files.DownloadAsync(sandbox.Name, "/tmp/dotnet-it/blob.bin", download, cancellationToken);
            await Assert.That(download.ToArray().SequenceEqual(payload)).IsTrue();

            // A directory tree, both ways.
            var source = Directory.CreateDirectory(Path.Join(local.FullName, "source"));
            Directory.CreateDirectory(Path.Join(source.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Join(source.FullName, "a.txt"), "alpha", cancellationToken);
            await File.WriteAllTextAsync(Path.Join(source.FullName, "nested", "b.txt"), "beta", cancellationToken);
            await client.Files.UploadDirectoryAsync(sandbox.Name, source.FullName, "/tmp/dotnet-it/tree", cancellationToken);
            await Assert.That((await sandbox.ShAsync("cat /tmp/dotnet-it/tree/a.txt /tmp/dotnet-it/tree/nested/b.txt", cancellationToken)).Trim()).IsEqualTo("alphabeta");

            var target = Path.Join(local.FullName, "target");
            await client.Files.DownloadDirectoryAsync(sandbox.Name, "/tmp/dotnet-it/tree", target, cancellationToken);
            await Assert.That(await File.ReadAllTextAsync(Path.Join(target, "a.txt"), cancellationToken)).IsEqualTo("alpha");
            await Assert.That(await File.ReadAllTextAsync(Path.Join(target, "nested", "b.txt"), cancellationToken)).IsEqualTo("beta");
        }
        finally
        {
            local.Delete(recursive: true);
        }
    }

    [Test]
    [Timeout(600_000)]
    public async Task Watch_reports_status_and_logs_are_readable(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-watch", cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        SandboxUpdated? first = null;
        await foreach (var e in client.Watch.WatchAsync(sandbox.Name, new WatchOptions { FollowStatus = true }, timeout.Token))
        {
            if (e is SandboxUpdated updated)
            {
                first = updated;
                break;
            }
        }

        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Sandbox.Name).IsEqualTo(sandbox.Name);
        await Assert.That(first.Sandbox.Phase).IsEqualTo(SandboxPhase.Ready);

        var logs = await client.Watch.GetLogsAsync(sandbox.Name, new LogOptions { Lines = 50 }, cancellationToken);
        await Assert.That(logs.Lines.Count).IsGreaterThan(0);
        await Assert.That(logs.Lines.All(l => l.SandboxId == sandbox.Sandbox.Id)).IsTrue();
    }

    [Test]
    [Timeout(600_000)]
    public async Task Stop_update_labels_and_start(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-update", cancellationToken);

        await client.Sandboxes.StopAsync(sandbox.Name, cancellationToken);
        var stopped = await client.Sandboxes.WaitStoppedAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(3) }, cancellationToken);
        await Assert.That(stopped.Phase).IsEqualTo(SandboxPhase.Stopped);

        // The gateway runs with enable_sandbox_updates; labels can change on every compute driver. Right after the
        // stop the gateway may still record status changes, and a compare-and-swap update then loses with Aborted
        // ("concurrent modification"), so re-read the version and retry, as a real caller would.
        Sandbox updated;
        ulong usedVersion;
        for (var attempt = 1; ; attempt++)
        {
            usedVersion = (await client.Sandboxes.GetAsync(sandbox.Name, cancellationToken)).ResourceVersion;
            try
            {
                updated = await client.Sandboxes.UpdateAsync(
                    sandbox.Name,
                    usedVersion,
                    new SandboxUpdate { Labels = new Dictionary<string, string> { ["sdk"] = "dotnet", ["stage"] = "updated" } },
                    cancellationToken: cancellationToken);
                break;
            }
            catch (OpenShellException e) when (e.IsConflict && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        await Assert.That(updated.Labels["stage"]).IsEqualTo("updated");
        await Assert.That(updated.ResourceVersion).IsGreaterThan(usedVersion);

        var stale = await Assert.That(async () => await client.Sandboxes.UpdateAsync(
            sandbox.Name, stopped.ResourceVersion, new SandboxUpdate { Labels = new Dictionary<string, string> { ["stage"] = "stale" } }, cancellationToken: cancellationToken))
            .Throws<OpenShellException>();
        await Assert.That(stale!.IsConflict).IsTrue();

        await client.Sandboxes.StartAsync(sandbox.Name, cancellationToken);
        await client.Sandboxes.WaitReadyAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken);
        await Assert.That((await sandbox.ShAsync("echo back", cancellationToken)).Trim()).IsEqualTo("back");
        await Assert.That((await client.Sandboxes.GetAsync(sandbox.Name, cancellationToken)).Labels["stage"]).IsEqualTo("updated");
    }

    [Test]
    [Timeout(600_000)]
    public async Task Suspend_is_rejected_by_the_docker_driver(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var info = await client.Health.GetGatewayInfoAsync(cancellationToken);
        if (!info.ComputeDrivers.Any(d => d.Name.Contains("docker", StringComparison.OrdinalIgnoreCase)))
        {
            Skip.Test("this checks the Docker driver's documented suspend rejection");
        }

        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-suspend", cancellationToken);

        var ex = await Assert.That(async () => await client.Sandboxes.SuspendAsync(sandbox.Name, cancellationToken)).Throws<OpenShellException>();

        // The Docker driver has no suspend; the gateway reports it as a failed precondition or unimplemented.
        await Assert.That(ex!.Kind is OpenShellErrorKind.Conflict or OpenShellErrorKind.Unimplemented).IsTrue();
        await Assert.That((await client.Sandboxes.GetAsync(sandbox.Name, cancellationToken)).Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    [Timeout(600_000)]
    public async Task Tcp_forward_and_listen_reach_a_sandbox_port(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-tcp", cancellationToken);
        const uint port = 18080;
        await sandbox.ShAsync($"setsid {EchoServer} {port} </dev/null >/dev/null 2>&1 &", cancellationToken);

        // ForwardAsync: a duplex stream to the sandbox port. Retry until the server is listening.
        string? echoed = null;
        for (var attempt = 0; attempt < 20 && echoed is null; attempt++)
        {
            try
            {
                await using var stream = await client.Tcp.ForwardAsync(sandbox.Name, port, cancellationToken: cancellationToken);
                echoed = await EchoAsync(stream, "forwarded", cancellationToken);
            }
            catch (Exception e) when (e is OpenShellException or IOException or EndOfStreamException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        await Assert.That(echoed).IsEqualTo("forwarded");

        // ListenAsync: a local port that forwards each connection.
        await using var listener = await client.Tcp.ListenAsync(sandbox.Name, port, cancellationToken: cancellationToken);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(listener.LocalEndPoint, cancellationToken);
        await Assert.That(await EchoAsync(tcp.GetStream(), "listened", cancellationToken)).IsEqualTo("listened");
    }

    [Test]
    [Timeout(600_000)]
    public async Task Ssh_tunnel_reaches_the_sandbox_ssh_server(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-ssh", cancellationToken);

        var session = await client.Ssh.CreateSessionAsync(sandbox.Name, cancellationToken);
        await Assert.That(session.SandboxId).IsEqualTo(sandbox.Sandbox.Id);
        await Assert.That(session.Token).IsNotEmpty();
        var revoked = await client.Ssh.RevokeSessionAsync(session.Token, cancellationToken: cancellationToken);
        await Assert.That(revoked.IsComplete || revoked.Outcome == DeletionOutcome.Accepted).IsTrue();

        // The relay carries raw SSH: the server speaks first with its identification string (RFC 4253 4.2).
        await using var tunnel = await client.Ssh.TunnelAsync(sandbox.Name, cancellationToken: cancellationToken);
        var banner = await ReadLineAsync(tunnel, cancellationToken);
        await Assert.That(banner).StartsWith("SSH-2.0-");
    }

    [Test]
    [Timeout(600_000)]
    public async Task Exec_python_runs_a_script(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-python", cancellationToken);
        var probe = await client.Exec.RunAsync(sandbox.Name, ["/bin/sh", "-c", "command -v python3"], cancellationToken: cancellationToken);
        if (!probe.Succeeded)
        {
            Skip.Test($"the sandbox image {GatewayFixture.Image} has no python3");
        }

        var result = await client.Exec.ExecPythonAsync(
            sandbox.Name,
            "import sys; print(sum(int(a) for a in sys.argv[1:]))",
            new PythonExecOptions { Interpreter = "python3", Arguments = ["2", "40"] },
            cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.StdoutText.Trim()).IsEqualTo("42");
    }

    private static async Task<string> EchoAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return await ReadLineAsync(stream, cancellationToken);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var line = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                throw new EndOfStreamException($"stream ended after {line.Count} bytes");
            }

            if (buffer[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
            }

            line.Add(buffer[0]);
        }
    }
}
