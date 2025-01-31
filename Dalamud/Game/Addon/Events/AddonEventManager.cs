﻿using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Addon.Events;

/// <summary>
/// Service provider for addon event management.
/// </summary>
[InterfaceVersion("1.0")]
[ServiceManager.EarlyLoadedService]
internal unsafe class AddonEventManager : IDisposable, IServiceType
{
    /// <summary>
    /// PluginName for Dalamud Internal use.
    /// </summary>
    public const string DalamudInternalKey = "Dalamud.Internal";
    
    private static readonly ModuleLog Log = new("AddonEventManager");
    
    [ServiceManager.ServiceDependency]
    private readonly AddonLifecycle addonLifecycle = Service<AddonLifecycle>.Get();

    private readonly AddonLifecycleEventListener finalizeEventListener;
    
    private readonly AddonEventManagerAddressResolver address;
    private readonly Hook<UpdateCursorDelegate> onUpdateCursor;

    private readonly List<PluginEventController> pluginEventControllers;
    
    private AddonCursorType? cursorOverride;
    
    [ServiceManager.ServiceConstructor]
    private AddonEventManager(TargetSigScanner sigScanner)
    {
        this.address = new AddonEventManagerAddressResolver();
        this.address.Setup(sigScanner);

        this.pluginEventControllers = new List<PluginEventController>
        {
            new(DalamudInternalKey), // Create entry for Dalamud's Internal Use.
        };
        
        this.cursorOverride = null;

        this.onUpdateCursor = Hook<UpdateCursorDelegate>.FromAddress(this.address.UpdateCursor, this.UpdateCursorDetour);

        this.finalizeEventListener = new AddonLifecycleEventListener(AddonEvent.PreFinalize, string.Empty, this.OnAddonFinalize);
        this.addonLifecycle.RegisterListener(this.finalizeEventListener);
    }

    private delegate nint UpdateCursorDelegate(RaptureAtkModule* module);

    /// <inheritdoc/>
    public void Dispose()
    {
        this.onUpdateCursor.Dispose();

        foreach (var pluginEventController in this.pluginEventControllers)
        {
            pluginEventController.Dispose();
        }
        
        this.addonLifecycle.UnregisterListener(this.finalizeEventListener);
    }

    /// <summary>
    /// Registers an event handler for the specified addon, node, and type.
    /// </summary>
    /// <param name="pluginId">Unique ID for this plugin.</param>
    /// <param name="atkUnitBase">The parent addon for this event.</param>
    /// <param name="atkResNode">The node that will trigger this event.</param>
    /// <param name="eventType">The event type for this event.</param>
    /// <param name="eventHandler">The handler to call when event is triggered.</param>
    /// <returns>IAddonEventHandle used to remove the event.</returns>
    internal IAddonEventHandle? AddEvent(string pluginId, IntPtr atkUnitBase, IntPtr atkResNode, AddonEventType eventType, IAddonEventManager.AddonEventHandler eventHandler)
    {
        if (this.pluginEventControllers.FirstOrDefault(entry => entry.PluginId == pluginId) is { } eventController)
        {
            return eventController.AddEvent(atkUnitBase, atkResNode, eventType, eventHandler);
        }
        
        Log.Verbose($"Unable to locate controller for {pluginId}. No event was added.");
        return null;
    }

    /// <summary>
    /// Unregisters an event handler with the specified event id and event type.
    /// </summary>
    /// <param name="pluginId">Unique ID for this plugin.</param>
    /// <param name="eventHandle">The Unique Id for this event.</param>
    internal void RemoveEvent(string pluginId, IAddonEventHandle eventHandle)
    {
        if (this.pluginEventControllers.FirstOrDefault(entry => entry.PluginId == pluginId) is { } eventController)
        {
            eventController.RemoveEvent(eventHandle);
        }
        else
        {
            Log.Verbose($"Unable to locate controller for {pluginId}. No event was removed.");
        }
    }
    
    /// <summary>
    /// Force the game cursor to be the specified cursor.
    /// </summary>
    /// <param name="cursor">Which cursor to use.</param>
    internal void SetCursor(AddonCursorType cursor) => this.cursorOverride = cursor;

    /// <summary>
    /// Un-forces the game cursor.
    /// </summary>
    internal void ResetCursor() => this.cursorOverride = null;

    /// <summary>
    /// Adds a new managed event controller if one doesn't already exist for this pluginId.
    /// </summary>
    /// <param name="pluginId">Unique ID for this plugin.</param>
    internal void AddPluginEventController(string pluginId)
    {
        if (this.pluginEventControllers.All(entry => entry.PluginId != pluginId))
        {
            Log.Verbose($"Creating new PluginEventController for: {pluginId}");
            this.pluginEventControllers.Add(new PluginEventController(pluginId));
        }
    }

    /// <summary>
    /// Removes an existing managed event controller for the specified plugin.
    /// </summary>
    /// <param name="pluginId">Unique ID for this plugin.</param>
    internal void RemovePluginEventController(string pluginId)
    {
        if (this.pluginEventControllers.FirstOrDefault(entry => entry.PluginId == pluginId) is { } controller)
        {
            Log.Verbose($"Removing PluginEventController for: {pluginId}");
            this.pluginEventControllers.Remove(controller);
            controller.Dispose();
        }
    }

    [ServiceManager.CallWhenServicesReady]
    private void ContinueConstruction()
    {
        this.onUpdateCursor.Enable();
    }

    /// <summary>
    /// When an addon finalizes, check it for any registered events, and unregister them.
    /// </summary>
    /// <param name="eventType">Event type that triggered this call.</param>
    /// <param name="addonInfo">Addon that triggered this call.</param>
    private void OnAddonFinalize(AddonEvent eventType, AddonArgs addonInfo)
    {
        // It shouldn't be possible for this event to be anything other than PreFinalize.
        if (eventType != AddonEvent.PreFinalize) return;

        foreach (var pluginList in this.pluginEventControllers)
        {
            pluginList.RemoveForAddon(addonInfo.AddonName);
        }
    }
    
    private nint UpdateCursorDetour(RaptureAtkModule* module)
    {
        try
        {
            var atkStage = AtkStage.GetSingleton();
            
            if (this.cursorOverride is not null && atkStage is not null)
            {
                var cursor = (AddonCursorType)atkStage->AtkCursor.Type;
                if (cursor != this.cursorOverride) 
                {
                    AtkStage.GetSingleton()->AtkCursor.SetCursorType((AtkCursor.CursorType)this.cursorOverride, 1);
                }
                
                return nint.Zero;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Exception in UpdateCursorDetour.");
        }

        return this.onUpdateCursor!.Original(module);
    }
}

/// <summary>
/// Plugin-scoped version of a AddonEventManager service.
/// </summary>
[PluginInterface]
[InterfaceVersion("1.0")]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IAddonEventManager>]
#pragma warning restore SA1015
internal class AddonEventManagerPluginScoped : IDisposable, IServiceType, IAddonEventManager
{
    [ServiceManager.ServiceDependency]
    private readonly AddonEventManager eventManagerService = Service<AddonEventManager>.Get();

    private readonly LocalPlugin plugin;

    private bool isForcingCursor;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddonEventManagerPluginScoped"/> class.
    /// </summary>
    /// <param name="plugin">Plugin info for the plugin that requested this service.</param>
    public AddonEventManagerPluginScoped(LocalPlugin plugin)
    {
        this.plugin = plugin;
        
        this.eventManagerService.AddPluginEventController(plugin.Manifest.WorkingPluginId.ToString());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // if multiple plugins force cursors and dispose without un-forcing them then all forces will be cleared.
        if (this.isForcingCursor)
        {
            this.eventManagerService.ResetCursor();
        }
        
        this.eventManagerService.RemovePluginEventController(this.plugin.Manifest.WorkingPluginId.ToString());
    }
    
    /// <inheritdoc/>
    public IAddonEventHandle? AddEvent(IntPtr atkUnitBase, IntPtr atkResNode, AddonEventType eventType, IAddonEventManager.AddonEventHandler eventHandler) 
        => this.eventManagerService.AddEvent(this.plugin.Manifest.WorkingPluginId.ToString(), atkUnitBase, atkResNode, eventType, eventHandler);

    /// <inheritdoc/>
    public void RemoveEvent(IAddonEventHandle eventHandle)
        => this.eventManagerService.RemoveEvent(this.plugin.Manifest.WorkingPluginId.ToString(), eventHandle);
    
    /// <inheritdoc/>
    public void SetCursor(AddonCursorType cursor)
    {
        this.isForcingCursor = true;
        
        this.eventManagerService.SetCursor(cursor);
    }
    
    /// <inheritdoc/>
    public void ResetCursor()
    {
        this.isForcingCursor = false;
        
        this.eventManagerService.ResetCursor();
    }
}
