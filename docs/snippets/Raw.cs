// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Openshell.Datamodel.V1;
using OpenShell;
using Wire = Openshell.V1;

namespace Snippets;

/// <summary>Snippets for docs/guides/raw-grpc.md.</summary>
internal static class Raw
{
    internal static async Task RawCallAsync(OpenShellClient client)
    {
        #region RawCall
        // The generated client shares the connection, authentication, retries and telemetry of `client`.
        // It takes and returns protobuf wire types, and its errors are RpcException, not OpenShellException.
        try
        {
            var response = await client.Raw.GetSandboxAsync(new Wire.GetSandboxRequest
            {
                Name = "agent-1",
                WorkspaceScope = new WorkspaceSelector { Workspace = client.Workspace },   // not filled in for you
            });
            Console.WriteLine(response.Sandbox);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            Console.WriteLine("no such sandbox");
        }
        #endregion
    }

    internal static SandboxSpec RawSpec()
    {
        #region RawSpec
        // Adjust the assembled wire spec for a field the curated SandboxSpec does not surface: here, a GPU request.
        var spec = new SandboxSpec
        {
            Image = "registry.example.com/team/cuda-agent:1.0",
            RawSpec = wire => wire.ResourceRequirements = new Wire.ResourceRequirements
            {
                Gpu = new Wire.GpuResourceRequirements { Count = 1 },
            },
        };
        #endregion
        return spec;
    }
}
