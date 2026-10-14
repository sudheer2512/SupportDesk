import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { PushDeviceDto, RegisterDeviceRequest } from '../models/mobile';

const enc = encodeURIComponent;

/**
 * Push-notification devices of the mobile app. Agents use `/me/devices`; portal contacts use
 * `/portal/{brand}/devices`. Register after every sign-in (and whenever the OS rotates the token);
 * unregister before signing out so the install stops receiving the previous user's alerts.
 */
@Injectable({ providedIn: 'root' })
export class DevicesApi {
  private readonly api = inject(ApiClient);

  list(): Observable<PushDeviceDto[]> {
    return this.api.get<PushDeviceDto[]>('me/devices', { silent: true });
  }

  register(request: RegisterDeviceRequest): Observable<PushDeviceDto> {
    return this.api.post<PushDeviceDto>('me/devices', request, { silent: true });
  }

  unregister(token: string): Observable<void> {
    return this.api.post<void>('me/devices/unregister', { token }, { silent: true });
  }

  listForContact(brand: string): Observable<PushDeviceDto[]> {
    return this.api.get<PushDeviceDto[]>(`portal/${enc(brand.toLowerCase())}/devices`, { silent: true });
  }

  registerForContact(brand: string, request: RegisterDeviceRequest): Observable<PushDeviceDto> {
    return this.api.post<PushDeviceDto>(`portal/${enc(brand.toLowerCase())}/devices`, request, { silent: true });
  }

  unregisterForContact(brand: string, token: string): Observable<void> {
    return this.api.post<void>(`portal/${enc(brand.toLowerCase())}/devices/unregister`, { token }, { silent: true });
  }
}
