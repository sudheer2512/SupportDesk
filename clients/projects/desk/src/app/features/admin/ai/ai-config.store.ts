import { Injectable, inject, signal } from '@angular/core';
import { AiAdminApi, AiConfigurationDto, UpdateAiConfigurationRequest, isApiError } from '@sd/core';
import { ToastService } from '@sd/ui';
import { Observable, firstValueFrom } from 'rxjs';

/**
 * The tenant's AI configuration shared by the AI settings pages (provided on the admin AI route). Saves send the
 * loaded rowVersion; a concurrent change reloads the latest values and tells the admin.
 */
@Injectable()
export class AiConfigStore {
  private readonly api = inject(AiAdminApi);
  private readonly toast = inject(ToastService);

  readonly config = signal<AiConfigurationDto | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  load(): void {
    this.loading.set(true);
    this.api.config().subscribe({
      next: (c) => {
        this.config.set(c);
        this.loading.set(false);
        this.error.set(null);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(isApiError(err) ? err.userMessage : 'Could not load the AI settings.');
      },
    });
  }

  /** Saves a partial update. Rejects with the ApiError (validation errors are shown by the caller). */
  async save(patch: UpdateAiConfigurationRequest, successMessage = 'AI settings saved'): Promise<AiConfigurationDto> {
    try {
      const saved = await firstValueFrom(this.api.updateConfig({ ...patch, rowVersion: this.config()?.rowVersion ?? null }) as Observable<AiConfigurationDto>);
      this.config.set(saved);
      this.toast.success(successMessage);
      return saved;
    } catch (err: unknown) {
      if (isApiError(err) && err.is('concurrency_conflict')) {
        this.toast.warning('Someone else changed the AI settings', 'The latest values were loaded — review and save again.');
        this.load();
      } else if (isApiError(err) && err.code !== 'validation_failed') {
        this.toast.error('The AI settings were not saved', err.userMessage);
      }
      throw err;
    }
  }
}
