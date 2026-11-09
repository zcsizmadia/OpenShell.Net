// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

// A default per-test timeout, so a hung test fails under its own name instead of stalling the run until the CI job
// times out. Generous on purpose: the slowest tests (property tests) finish in seconds; a class or method can still
// override it with its own [Timeout].
[assembly: Timeout(120_000)]
