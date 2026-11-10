namespace BossMod.Autorotation;

// Implemented by the optional module config assembly so the core autorotation host can
// consume this option without taking a compile-time dependency on BossMod.Modules.
public interface IAutoDungeonAutorotationConfig
{
    bool AutoActivateDungeonAI { get; set; }
    bool AutoLeaveAfterBossDefeat { get; set; }
}
