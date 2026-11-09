<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Workspaces and pagination

## Workspaces

A workspace is the isolation boundary for sandboxes, providers, templates, and the other resources. Every operation runs in one: `OpenShellClientOptions.DefaultWorkspace`, which defaults to `default`. <xref:OpenShell.IOpenShellClient.InWorkspace*> returns a view bound to another workspace that shares the same connection; disposing the view does not close the connection.

`client.Workspaces` (<xref:OpenShell.IWorkspaceClient>) creates, lists, and deletes workspaces, and manages their members (<xref:OpenShell.WorkspaceRole>: `User` or `Admin`).

[!code-csharp[](../snippets/ClientBehavior.cs#Workspaces)]

## Pagination

List methods return a lazy <xref:OpenShell.Pager`1>: nothing is fetched until you enumerate it, and each page is fetched as you reach it. Enumerate it for items, call `PagesAsync()` for <xref:OpenShell.Page`1>s, or `ToListAsync()` for everything.

[!code-csharp[](../snippets/ClientBehavior.cs#Pagination)]

<xref:OpenShell.ListOptions> sets the page size (0 leaves it to the gateway), a starting page token, a label selector (`team=platform`), and `AllWorkspaces` to list across every workspace the caller can see (sandboxes, templates, providers, and services). Each enumeration starts over from the first page (or `PageToken`).
