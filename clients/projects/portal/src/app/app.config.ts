import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TitleStrategy, provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { PortalAuthService, SD_AUTH_SESSION, ThemeService, authInterceptor, errorToastInterceptor, provideSdCore } from '@sd/core';
import { ToastService } from '@sd/ui';
import { routes } from './app.routes';
import { PortalTitleStrategy } from './portal-title.strategy';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    provideHttpClient(withInterceptors([errorToastInterceptor, authInterceptor])),
    provideSdCore({ apiBaseUrl: '/api/v1', client: 'web' }),
    // The portal has its own token store, separate from the agent session.
    { provide: SD_AUTH_SESSION, useExisting: PortalAuthService },
    { provide: TitleStrategy, useClass: PortalTitleStrategy },
    provideAppInitializer(() => {
      inject(ThemeService);
      inject(ToastService);
    }),
  ],
};
