using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using GilMaster.Core;
using GilMaster.Windows;

namespace GilMaster;

public sealed class Plugin : IDalamudPlugin
{
    public static Configuration Config { get; private set; } = null!;
    public static ProfitEngine ProfitEngine { get; private set; } = null!;
    public static RecipeResolver RecipeResolver { get; private set; } = null!;
    public static GatheringLocator GatheringLocator { get; private set; } = null!;
    public static LevelingAdvisor LevelingAdvisor { get; private set; } = null!;
    public static CraftExecutor      CraftExecutor      { get; private set; } = null!;
    public static CraftStarter       CraftStarter       { get; private set; } = null!;
    public static CraftQueue         CraftQueue         { get; private set; } = null!;
    public static CraftQueueExecutor CraftQueueExecutor { get; private set; } = null!;
    public static ArtisanIpc         Artisan            { get; private set; } = null!;
    public static AllaganToolsBridge AllaganTools       { get; private set; } = null!;
    public static MateriaExtractor   MateriaExtractor   { get; private set; } = null!;
    public static SellEngine         SellEngine         { get; private set; } = null!;
    public static FlipEngine         FlipEngine         { get; private set; } = null!;
    public static CraftSimRunner     CraftSim           { get; private set; } = null!;
    public static FurnitureEngine    FurnitureEngine    { get; private set; } = null!;
    public static GearEngine         GearEngine         { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("GilMaster");
    private readonly MainWindow mainWindow;
    private readonly GcSupplyOverlay gcSupplyOverlay = new();
    private readonly CraftListContextMenu contextMenu = new();

    private const string Command = "/gilmaster";
    private const string CommandAlias = "/gil";

    public Plugin(IDalamudPluginInterface pi)
    {
        Service.Initialize(pi);
        ECommonsMain.Init(pi, this); // addon-automation foundation (absorbed from Artisan)

        Config = Service.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        GatheringLocator = new GatheringLocator();
        RecipeResolver = new RecipeResolver();
        RecipeResolver.SetGatherables(GatheringLocator.GatherableItemIds);

        ProfitEngine = new ProfitEngine();
        LevelingAdvisor = new LevelingAdvisor();
        CraftExecutor      = new CraftExecutor();
        CraftStarter       = new CraftStarter();
        CraftQueue         = new CraftQueue();
        CraftQueueExecutor = new CraftQueueExecutor();
        Artisan            = new ArtisanIpc();
        AllaganTools       = new AllaganToolsBridge();
        MateriaExtractor   = new MateriaExtractor();
        SellEngine         = new SellEngine();
        FlipEngine         = new FlipEngine();
        CraftSim           = new CraftSimRunner();
        FurnitureEngine    = new FurnitureEngine();
        GearEngine         = new GearEngine();

        // Load last scan + learned crafting rotations from disk
        ProfitEngine.TryLoadCache();
        RotationCache.Load();

        // Warm the vendor price + location indexes off-thread (full-sheet scans of GilShopItem /
        // ENpcBase / Level) so the Queue tab's "Buy from NPC" buttons never hitch on first use.
        System.Threading.Tasks.Task.Run(() => { _ = VendorPrices.Count; _ = VendorLocations.Count; });

        mainWindow = new MainWindow();
        windowSystem.AddWindow(mainWindow);
        // Anchored button on the native GC Delivery Missions window (drawn via windowSystem.Draw).
        windowSystem.AddWindow(gcSupplyOverlay);

        Service.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GilMaster — find profitable items to gather and craft.",
        });
        Service.CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /gilmaster.",
        });

        Service.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        Service.PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;

        contextMenu.Enable();
    }

    private void OnCommand(string command, string args) => OpenMainWindow();
    private void OpenMainWindow() => mainWindow.IsOpen = true;
    private void OpenSettings() { mainWindow.IsOpen = true; MainWindow.OpenToSettings(); }

    public void Dispose()
    {
        Service.CommandManager.RemoveHandler(Command);
        Service.CommandManager.RemoveHandler(CommandAlias);
        Service.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        contextMenu.Dispose();
        MateriaExtractor.Dispose();
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        CraftQueueExecutor.Dispose();
        CraftExecutor.Dispose();
        CraftStarter.Dispose();
        SellEngine.Dispose();
        FlipEngine.Dispose();
        CraftSim.Dispose();
        FurnitureEngine.Dispose();
        ProfitEngine.Dispose();
        Config.Save();
        ECommonsMain.Dispose();
    }
}
