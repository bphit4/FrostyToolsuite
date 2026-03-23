using FrostyEditor.Models.Audio;

namespace FrostyEditor.Managers;

internal readonly record struct SpsCandidate(long Offset, SpsSoundHeader Header, bool StrictMatch);
