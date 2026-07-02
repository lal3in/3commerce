using Microsoft.AspNetCore.Components.Web;

namespace ThreeCommerce.Admin.Components;

/// <summary>
/// Shared render modes for admin pages. <see cref="InteractiveServerNoPrerender"/> is InteractiveServer
/// with prerendering OFF: the admin is an internal, auth-gated tool, so we skip SSR/prerender and let the
/// SignalR circuit do the first render. That removes the pre-circuit window where a prerendered button is
/// visible but has no wired handler yet (clicks silently do nothing), and it runs OnInitializedAsync once
/// instead of twice.
/// </summary>
public static class AdminRenderModes
{
    public static readonly InteractiveServerRenderMode InteractiveServerNoPrerender = new(prerender: false);
}
