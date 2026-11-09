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
- `DownloadDirectoryAsync` refuses, with `InvalidDataException`, archive entries that would land outside the target directory, and links. On Windows it also refuses entry names that Windows would not store as given: a path segment that contains `:` (an NTFS alternate data stream) or another character Windows forbids (`< > " | ? *`, control characters), that ends in a dot or a space, or that is a reserved device name such as `CON`, `NUL`, `COM1` or `LPT1`, with or without an extension.
- A remote `tar` that fails during an upload throws <xref:OpenShell.OpenShellException> with `Kind` `Internal`, its exit code, and its error output, also when it exits before reading all of the input (for example because it cannot create the target directory).
- A remote command that fails during a download, for example because the remote path does not exist, throws <xref:OpenShell.OpenShellException> with `Kind` `Internal` and the message `remote command exited with code …`, followed by its error output; it is not `NotFound`. A download whose archive is truncated, empty, or unexpected, or that holds an entry the download refuses, throws `InvalidDataException`.
- A missing local source throws `FileNotFoundException` (`UploadAsync`) or `DirectoryNotFoundException` (`UploadDirectoryAsync`).
- Transfers are streaming calls, so `DefaultTimeout` does not bound them; pass a `CancellationToken` (see [Retries and timeouts](retries.md#timeouts)).

## Rootfs staging

Compute drivers that boot from a root filesystem archive (for example the VM driver) read it from a gateway staging slot. `client.Rootfs` (<xref:OpenShell.IRootfsClient>) stages a local tar archive and returns a token; <xref:OpenShell.RootfsDriverConfig.WithStagingToken*?displayProperty=nameWithType> puts the token into the template's driver configuration.

[!code-csharp[](../snippets/FilesAndRootfs.cs#Rootfs)]

Only a client that shares the gateway's filesystem can complete the copy, so this is a local-gateway workflow. The gateway enforces a size limit and reclaims unused slots (`RootfsStagingSlot.ExpiresAt`). `BeginStagingAsync` allocates a slot without copying, when you want to write the archive yourself. `WithStagingToken` keeps any settings already in the driver configuration JSON you pass, and throws when it already contains a token.
