import { Injectable, OnDestroy } from '@angular/core';

@Injectable({
  providedIn: 'root',
})
export class AudioFeedback implements OnDestroy {
  private context: AudioContext | null = null;

  playBeeps(count: number): void {
    const AudioContextType = window.AudioContext;
    if (!AudioContextType) {
      return;
    }

    // Reuse one context for this app lifetime. Never let an unavailable
    // audio device interrupt booking callbacks or their busy-state cleanup.
    try {
      if (!this.context || this.context.state === 'closed') this.context = new AudioContextType();
      const context = this.context;
      if (context.state === 'suspended') void context.resume().catch(() => this.dispose(context));
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
        oscillator.onended = () => {
          oscillator.disconnect();
          gain.disconnect();
        };
        oscillator.start(startAt);
        oscillator.stop(stopAt);
      }
    } catch {
      if (this.context) this.dispose(this.context);
    }
  }

  ngOnDestroy(): void {
    if (this.context) this.dispose(this.context);
  }

  private dispose(context: AudioContext): void {
    if (this.context === context) this.context = null;
    try {
      void context.close().catch(() => undefined);
    } catch {
      /* Audio is best effort. */
    }
  }
}
