import { Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-avatar',
  imports: [],
  templateUrl: './avatar.html',
  styleUrl: './avatar.scss',
})
export class Avatar {
  readonly displayName = input('');
  readonly initials = input<string | null>(null);
  readonly color = input('#5b6375');
  readonly imageUrl = input<string | null>(null);
  readonly size = input<'normal' | 'large' | 'small'>('normal');

  readonly fallbackText = computed(() => {
    const initials = this.initials();
    if (initials) {
      return initials;
    }

    return this.displayName().slice(0, 2).toUpperCase();
  });
}
