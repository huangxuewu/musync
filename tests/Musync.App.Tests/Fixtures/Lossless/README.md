These fixtures contain an original 0.1-second 440 Hz sine tone generated for source-format tests. They are never played to a speaker.

Generated with the project's bundled FFmpeg:

```text
ffmpeg -f lavfi -i sine=frequency=440:sample_rate=44100:duration=0.1 -c:a alac tone-alac.m4a
ffmpeg -f lavfi -i sine=frequency=440:sample_rate=44100:duration=0.1 -c:a adpcm_ima_wav tone-adpcm.wav
```

ALAC confirms that a lossless M4A source is recognized by codec. ADPCM confirms that a WAV extension alone does not produce a lossless badge. Other format fixtures are generated in temporary test directories by NAudio.
