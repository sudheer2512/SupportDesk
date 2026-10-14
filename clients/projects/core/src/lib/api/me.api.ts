import { Injectable, inject } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { MeStore } from '../auth/me.store';
import { ApiClient } from '../http/api-client';
import { MeDto, UpdateProfileRequest } from '../models/auth';
import { Availability } from '../models/enums';

/** GET /me and the signed-in user's own settings. Successful writes are reflected in MeStore. */
@Injectable({ providedIn: 'root' })
export class MeApi {
  private readonly api = inject(ApiClient);
  private readonly store = inject(MeStore);

  get(): Observable<MeDto> {
    return this.api.get<MeDto>('me').pipe(tap((me) => this.store.set(me)));
  }

  setAvailability(availability: Availability): Observable<void> {
    return this.api.put<void>('me/availability', { availability }).pipe(tap(() => this.store.patch({ availability })));
  }

  updateProfile(request: UpdateProfileRequest): Observable<void> {
    return this.api.put<void>('me/profile', request, { silent: true }).pipe(
      tap(() =>
        this.store.patch({
          displayName: request.displayName,
          timeZone: request.timeZone,
          locale: request.locale,
          signature: request.signature,
        }),
      ),
    );
  }
}
