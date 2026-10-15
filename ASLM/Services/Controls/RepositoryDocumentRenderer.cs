// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Controls;

/// <summary>
/// Applies the application palette to GitHub-rendered documentation without enabling repository scripts.
/// </summary>
internal static class RepositoryDocumentRenderer
{
    /// <summary>
    /// Wraps the rendered fragment in a responsive, passive document suitable for an embedded preview.
    /// </summary>
    internal static string Build(string html, string foreground, string background, string link, string separator) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <meta name="referrer" content="no-referrer">
        <style>
        html,body { margin:0; padding:0; background:{{background}}; color:{{foreground}}; }
        body { padding:14px; font:14px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif; overflow-wrap:anywhere; }
        * { box-sizing:border-box; max-width:100%; }
        a { color:{{link}}; } img { height:auto; } svg { max-width:100%; }
        h1,h2 { border-bottom:1px solid {{separator}}; padding-bottom:.3em; }
        h1 { font-size:2em; } h2 { font-size:1.5em; } h3 { font-size:1.25em; }
        pre { white-space:pre-wrap; overflow-wrap:anywhere; padding:12px; border:1px solid {{separator}}; border-radius:6px; }
        code { font-family:ui-monospace,Consolas,monospace; font-size:.9em; }
        blockquote { margin-left:0; padding-left:12px; border-left:3px solid {{separator}}; }
        table { border-collapse:collapse; display:block; overflow-x:auto; }
        td,th { border:1px solid {{separator}}; padding:6px 10px; }
        hr { border:0; border-top:1px solid {{separator}}; }
        .anchor { display:none; } body > :first-child { margin-top:0; } body > :last-child { margin-bottom:0; }
        ::-webkit-scrollbar { display:none; }
        </style></head><body>{{html}}</body></html>
        """;
}
