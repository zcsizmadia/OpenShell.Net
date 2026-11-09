// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/files-and-rootfs.md.</summary>
internal static class FilesAndRootfs
{
    internal static async Task FilesAsync(IOpenShellClient client)
    {
        #region Files
        // Single files: the remote path names the file, and its parent directory is created.
        await client.Files.UploadAsync("agent-1", "settings.json", "/workspace/config/settings.json");
        await client.Files.DownloadAsync("agent-1", "/workspace/out/report.csv", "report.csv");

        // Streams, with the Unix mode of the uploaded file.
        using (var script = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho ready\n")))
        {
            await client.Files.UploadAsync(
                "agent-1",
                script,
                "/workspace/bin/start.sh",
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using (var destination = File.Create("app.log"))
        {
            await client.Files.DownloadAsync("agent-1", "/var/log/app.log", destination);
        }

        // Whole directories.
        await client.Files.UploadDirectoryAsync("agent-1", "./src", "/workspace/src");
        await client.Files.DownloadDirectoryAsync("agent-1", "/workspace/out", "./out");
        #endregion
    }

    internal static async Task RootfsAsync(IOpenShellClient client)
    {
        #region Rootfs
        // Copies the archive into a gateway staging slot (the client must share the gateway's filesystem)
        // and returns the token that names it.
        var token = await client.Rootfs.StageAsync("/srv/images/agent-rootfs.tar");

        // Boot a sandbox from it: the token goes into the driver config of the template.
        var sandbox = await client.Sandboxes.CreateAsync("vm-agent", new SandboxSpec
        {
            Template = new SandboxTemplateSpec
            {
                DriverConfigJson = RootfsDriverConfig.WithStagingToken(token),
            },
        });
        #endregion
        Console.WriteLine(sandbox.Name);
    }
}
