import { ApplicationConfig, isDevMode, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { provideServiceWorker } from '@angular/service-worker';

import { routes } from './app.routes';
import { kioskDiagnosticsInterceptor } from './core/services/kiosk-diagnostics';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([kioskDiagnosticsInterceptor])),
    provideRouter(routes),
    // Hält die App lokal vor: ein Kiosk, der ohne Netz neu startet (nächtlicher
    // Reboot, Stromausfall), lädt sie trotzdem und kann offline stempeln. /api
    // wird nie gecacht (ngsw-config.json ohne dataGroups).
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ]
};
