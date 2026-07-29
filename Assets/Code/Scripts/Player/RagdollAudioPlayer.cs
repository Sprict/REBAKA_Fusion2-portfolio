using UnityEngine;
using Random = UnityEngine.Random;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollAudioPlayer
    {
        private readonly RagdollProfile _profile;
        private readonly AudioSource _soundSource;

        public RagdollAudioPlayer(RagdollProfile profile, AudioSource soundSource)
        {
            _profile = profile;
            _soundSource = soundSource;
        }

        public void PlayImpactSound()
        {
            PlayRandomClip(_profile != null ? _profile.impactSounds : null);
        }

        public void PlayHitSound()
        {
            PlayRandomClip(_profile != null ? _profile.hitSounds : null);
        }

        private void PlayRandomClip(AudioClip[] clips)
        {
            if (_soundSource == null || clips is not { Length: > 0 })
                return;

            _soundSource.PlayOneShot(clips[Random.Range(0, clips.Length)]);
        }
    }
}
