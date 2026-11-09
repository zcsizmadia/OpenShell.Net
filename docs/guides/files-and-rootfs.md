<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Files and rootfs

## File transfer

`client.Files` (<xref:OpenShell.IFileClient>) copies files and directories between the local machine and a sandbox. The files travel as a tar stream over exec, so the sandbox image needs `tar` and `sh`.

[!code-csharp[](../snippets/FilesAndRootfs.cs#Files)]

- Uploads create the remote parent directories. A remote file path must name a file, not end with `/`.
- A non-seekable upload stream is buffered in memory first, because the tar header needs the size up front. Uploaded stream files default to mode `0644` unless you pass one.
- `DownloadAsync` to a path writes a temporary file next to the target and replaces the target only on success.
- `DownloadDirectoryAsync` refuses archive entries that would land outside the target directory, and links.
- A failing remote `tar` during an upload throws <xref:OpenShell.OpenShellException> with its exit code and error output; a download whose archive is truncated, empty, or unexpected throws `InvalidDataException`.

## Rootfs staging

Compute drivers that boot from a root filesystem archive (for example the VM driver) read it from a gateway staging slot. `client.Rootfs` (<xref:OpenShell.IRootfsClient>) stages a local tar archive and returns a token; <xref:OpenShell.RootfsDriverConfig.WithStagingToken*?displayProperty=nameWithType> puts the token into the template's driver configuration.

[!code-csharp[](../snippets/FilesAndRootfs.cs#Rootfs)]

Only a client that shares the gateway's filesystem can complete the copy, so this is a local-gateway workflow. The gateway enforces a size limit and reclaims unused slots (`RootfsStagingSlot.ExpiresAt`). `BeginStagingAsync` allocates a slot without copying, when you want to write the archive yourself. `WithStagingToken` keeps any settings already in the driver configuration JSON you pass, and throws when it already contains a token.
