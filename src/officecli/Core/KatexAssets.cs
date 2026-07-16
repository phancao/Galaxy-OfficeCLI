// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

namespace OfficeCli.Core;

/// <summary>
/// Single source of truth for KaTeX asset URLs in generated HTML.
/// Mirrors the mermaid sourcing policy (see <c>MermaidImageRenderer</c>):
/// own mirror first (no third-party dependency at steady state, reachable in
/// networks where jsdelivr is not), public CDN as the fallback. The mirror
/// hosts the full dist subtree (js + css + fonts/) under one immutable
/// versioned prefix, so the css's relative <c>url(fonts/…)</c> references
/// resolve against whichever origin actually served it.
///
/// Unlike mermaid there is no local file cache: mermaid.js is executed by
/// the CLI's own headless browser against a throwaway file, while these URLs
/// are baked into preview HTML that users keep and open elsewhere — a
/// <c>file://~/.officecli/…</c> reference would break the moment the HTML
/// leaves the machine. Load robustness comes from the mirror→CDN onerror
/// chain plus the screenshot path's --virtual-time-budget/--timeout caps.
/// </summary>
internal static class KatexAssets
{
    public const string Version = "0.16.11";

    // GALAXY FORK: never reference the upstream project's mirror. The mirror
    // slot is filled from OFFICECLI_ASSET_MIRROR_BASE (a Galaxy-hosted static
    // prefix serving the katex-<version>/ dist subtree); when unset the public
    // CDN serves both slots, so generated HTML carries no third-party-VPS URL.
    private static readonly string MirrorBase =
        Environment.GetEnvironmentVariable("OFFICECLI_ASSET_MIRROR_BASE") is { Length: > 0 } b
            ? b.TrimEnd('/') + "/katex-" + Version
            : "https://cdn.jsdelivr.net/npm/katex@" + Version + "/dist";
    private const string CdnBase = "https://cdn.jsdelivr.net/npm/katex@" + Version + "/dist";

    public static string CssUrl => MirrorBase + "/katex.min.css";
    public static string JsUrl => MirrorBase + "/katex.min.js";
    public static string CdnCssUrl => CdnBase + "/katex.min.css";
    public static string CdnJsUrl => CdnBase + "/katex.min.js";

    /// <summary>
    /// onerror body for the stylesheet &lt;link&gt;: first failure retries the
    /// public CDN, second failure removes the tag (KaTeX js falls back to the
    /// caller-provided plain-text rendering, so a missing css is cosmetic).
    /// </summary>
    public static string CssOnErrorJs =>
        $"if(!this.dataset.f){{this.dataset.f=1;this.href='{CdnCssUrl}'}}else{{this.remove()}}";

    /// <summary>
    /// onerror body for the &lt;script&gt; tag: first failure injects a CDN
    /// copy whose own failure runs <paramref name="finalFallbackJs"/> (each
    /// call site keeps its existing degraded-rendering behavior).
    /// </summary>
    public static string JsOnErrorJs(string finalFallbackJs) =>
        "var s=document.createElement('script');s.src='" + CdnJsUrl + "';"
        + "s.onerror=function(){" + finalFallbackJs + "};document.head.appendChild(s)";
}
