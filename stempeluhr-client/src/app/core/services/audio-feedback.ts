import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root',
})
export class AudioFeedback {
  playBeeps(count: number): void {
    const AudioContextType = window.AudioContext;
    if (!AudioContextType) {
      return;
    }

    const context = new AudioContextType();
    for (let index = 0; index < count; index++) {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      const startAt = context.currentTime + index * 0.22;
      const stopAt = startAt + 0.11;

      oscillator.type = 'sine';
      oscillator.frequency.value = count === 1 ? 880 : 360;
      gain.gain.setValueAtTime(0.0001, startAt);
      gain.gain.exponentialRampToValueAtTime(0.2, startAt + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, stopAt);
      oscillator.connect(gain);
      gain.connect(context.destination);
      oscillator.start(startAt);
      oscillator.stop(stopAt);
    }
  }
}
