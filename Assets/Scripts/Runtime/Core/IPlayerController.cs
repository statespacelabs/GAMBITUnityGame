/// <summary>
/// Interface that all player controllers must implement.
/// HumanController, ScriptedBotController, and RLAgentController
/// all produce a PlayerCommand each frame through this interface.
/// </summary>
public interface IPlayerController
{
    /// <summary>
    /// Called every frame by PlayerBody to get the current command.
    /// Must never return null — return PlayerCommand.NoOp if idle.
    /// </summary>
    PlayerCommand GetCommand();

    /// <summary>
    /// Called once when this controller is assigned to a PlayerBody.
    /// Use this to cache references to identity, match manager, etc.
    /// </summary>
    void Initialize(PlayerIdentity identity, MatchManager matchManager);
}
