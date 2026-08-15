using System;
using System.Collections.Generic;

[Serializable]
public sealed class GambitTrajectoryVector3
{
    public float x;
    public float y;
    public float z;
}

[Serializable]
public sealed class GambitTrajectorySeries<T>
{
    public List<float> key = new List<float>();
    public List<T> value = new List<T>();
}

[Serializable]
public sealed class GambitTrajectoryPlayer
{
    public GambitTrajectorySeries<GambitTrajectoryVector3> position;
    public GambitTrajectorySeries<GambitTrajectoryVector3> rotation;
    public GambitTrajectorySeries<float> fov;
    public GambitTrajectorySeries<int> health;
    public GambitTrajectorySeries<bool> firstPerson;
}

[Serializable]
public sealed class GambitTrajectoryTarget
{
    public int id;
    public int type;
    public float spawnTime;
    public float destroyTime;
    public float timeToLive;
    public GambitTrajectorySeries<GambitTrajectoryVector3> position;
    public GambitTrajectorySeries<GambitTrajectoryVector3> rotation;
    public GambitTrajectorySeries<GambitTrajectoryVector3> scaling;
    public GambitTrajectorySeries<bool> visible;
}

[Serializable]
public sealed class GambitTrajectoryEvent
{
    public float time;
    public int type;
    public int senderID;
    public int receiverID;
}

[Serializable]
public sealed class GambitTrajectorySession
{
    public GambitTrajectoryPlayer player;
    public List<GambitTrajectoryTarget> targets = new List<GambitTrajectoryTarget>();
    public List<GambitTrajectoryEvent> events = new List<GambitTrajectoryEvent>();
}

[Serializable]
public sealed class GambitTrajectoryRenderManifest
{
    public string schema_version = "gambit_trajectory_render_v1";
    public string trajectory_path;
    public string trajectory_sha256;
    public string map_id;
    public string map_asset_sha256;
    public string camera;
    public string video_file;
    public int width;
    public int height;
    public int frame_rate;
    public int frame_count;
    public float duration_seconds;
}
