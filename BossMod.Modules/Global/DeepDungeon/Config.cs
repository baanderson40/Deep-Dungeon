using BossMod.Autorotation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;

namespace BossMod.Global.DeepDungeon;

[ConfigDisplay(Name = "Auto-DeepDungeon (Experimental)", Parent = typeof(ModuleConfig))]
public class AutoDDConfig : ConfigNode
{
    public enum ClearBehavior
    {
        [PropertyDisplay("Do not auto target")]
        None,
        [PropertyDisplay("Stop when passage opens")]
        Passage,
        [PropertyDisplay("Target everything if not at level cap, otherwise stop when passage opens")]
        Leveling,
        [PropertyDisplay("Target everything")]
        All,
    }

    [PropertyDisplay("Enable module", tooltip: "This is experimental. Use at your own risk. You will sometimes need to help the AI to navigate.")]
    public bool Enable = true;
    [PropertyDisplay("Enable minimap")]
    public bool EnableMinimap = true;
    [PropertyDisplay("Minimap scale")]
    [PropertySlider(0.2f, 3)]
    public float MinimapScale = 1.0f;
    [PropertyDisplay("Try to avoid traps", tooltip: "Avoid known trap locations sourced from Palantir data. (Traps revealed by a Pomander of Sight will always be avoided.)")]
    public bool TrapHints = true;
    [PropertyDisplay("Draw potential trap locations in game world")]
    public bool DrawTraps = false;

    [PropertyDisplay("Open chests")]
    public bool OpenTreasure = true;

    [PropertyDisplay("Automatic mob targeting behavior")]
    public ClearBehavior AutoClear = ClearBehavior.Leveling;

    [PropertyDisplay("Disable DoTs on non-boss floors (only affects VBM autorotation)")]
    public bool ForbidDOTs = false;

    [PropertyDisplay("Max number of mobs to pull before pausing navigation (0 = do not navigate while in combat)")]
    [PropertySlider(0, 15)]
    public int MaxPull = 0;
    [PropertyDisplay("Try to use terrain to LOS attacks", tooltip: "This feature is currently very inconsistent and will likely get you killed. Enabling it is not recommended.")]
    public bool AutoLOS = false;

    [PropertyDisplay("Reveal all rooms before proceeding to next floor")]
    public bool FullClear = false;

    public BitMask AutoPoms;
    public BitMask AutoMagicite;
    public BitMask AutoDemiclone;
    [SectionStart("Active buff maintenance")]
    [PropertyDisplay("Maintain Strength", tooltip: "Maintain Strength in and out of combat. Preserve one pomander on normal floors; allow the reserved last charge on boss-floor arrival. Never refresh while its effect is active.")]
    public bool MaintainStrength;
    [PropertyDisplay("Maintain Steel", tooltip: "Maintain Steel in and out of combat. Preserve one pomander on normal floors; allow the reserved last charge on boss-floor arrival. Never refresh while its effect is active.")]
    public bool MaintainSteel;
    [PropertyDisplay("Maintain Raising", tooltip: "Maintain Raising protection in and out of combat. May spend the last charge on any floor. Uses the pomander's active item-effect flag as protection evidence.")]
    public bool MaintainRaising;
    [PropertyDisplay("Use Safety at floor start", tooltip: "Use Safety once during the first 45 seconds of an initialized non-boss floor, even if it is the last charge. Mid-floor pickups wait until a later floor. Never use on boss floors. System log 7255 confirms use.")]
    public bool AutoSafety;
    [PropertyDisplay("Use Sight at floor start", tooltip: "Use Sight during the first 45 seconds of an initialized non-boss floor as fallback when Safety is unavailable. Safety takes priority; never automatically use both on one floor. System log 7256 confirms use.")]
    public bool AutoSight;
    [PropertyDisplay("Use Serenity for selected floor effects", tooltip: "Opt-in; detects selected player statuses and Gloom when current-floor floor-effect data is known. Serenity removes every floor enchantment, including beneficial effects. Auto-heal Penalty can be beneficial with EO Storms. Item Penalty does not prevent Serenity.")]
    public bool AutoSerenity;
    [PropertyDisplay("Serenity: Item Penalty")]
    public bool SerenityOnItemPenalty = true;
    [PropertyDisplay("Serenity: Amnesia")]
    public bool SerenityOnAmnesia = true;
    [PropertyDisplay("Serenity: Gloom")]
    public bool SerenityOnGloom;
    [PropertyDisplay("Serenity: Damage Down")]
    public bool SerenityOnDamageDown = true;
    [PropertyDisplay("Serenity: HP Penalty")]
    public bool SerenityOnHPPenalty = true;
    [PropertyDisplay("Serenity: Auto-heal Penalty")]
    public bool SerenityOnAutoHealPenalty = true;
    [PropertyDisplay("Use Affluence before eligible floor transitions", tooltip: "Use once before leaving for an eligible non-boss floor. May spend the last charge. Serenity and next-floor pomanders remain opt-in.")]
    public bool AutoAffluence;
    [PropertyDisplay("Use Flight before eligible floor transitions", tooltip: "Use once before leaving for an eligible non-boss floor. May spend the last charge. Flight halves enemies and passage kill requirements, rounded up.")]
    public bool AutoFlight;
    [PropertyDisplay("Use Fortune at floor start", tooltip: "Use during the first 45 seconds of an initialized non-boss floor when Fortune's item effect is not active. Mid-floor pickups wait until a later floor. May spend the last charge.")]
    public bool AutoFortune;
    [PropertyDisplay("Use Intuition on normal floors", tooltip: "Use once per non-boss floor when detection is not active and the current-floor hoard has not already been found. May spend the last charge. Active detection can carry across floor transitions.")]
    public bool AutoIntuition;

    public override void DrawCustom(string? page, UITree tree, WorldState ws, RotationDatabase? db)
    {
        UIMisc.SectionHeader("Auto-poms");

        using var _ = ImRaii.PushIndent(ImGui.GetFrameHeight());

        ImGui.Spacing();

        ImGui.TextWrapped("Selected pomanders will be used when a gold chest contains one that you can't carry. Disabled while in parties.");

        ImGui.Spacing();

        for (var i = 1; i < (int)PomanderID.Count; i++)
            using (ImRaii.PushId($"pom{i}"))
            {
                var row = Service.LuminaRow<DeepDungeonItem>((uint)i)!.Value;
                var wrap = Service.Texture.GetFromGameIcon(row.Icon).GetWrapOrEmpty();
                var scale = new Vector2(32 * ImGuiHelpers.GlobalScale);
                ImGui.Image(wrap.Handle, scale, new Vector2(0), tintCol: new Vector4(1) with { W = AutoPoms[i] ? 1 : 0.4f });
                if (ImGui.IsItemClicked())
                {
                    AutoPoms.Toggle(i);
                    Modified.Fire();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(row.Name.ToString());
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
                if (i % 8 > 0)
                    ImGui.SameLine();
            }

        ImGui.Text(""); // undo last sameline

        ImGui.Spacing();
    }
}
