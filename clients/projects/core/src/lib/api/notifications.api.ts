import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import { NotificationDto } from '../models/notifications';

/** In-app notifications (`/notifications`). */
@Injectable({ providedIn: 'root' })
export class NotificationsApi {
  private readonly api = inject(ApiClient);

  list(limit = 20, unreadOnly = false): Observable<NotificationDto[]> {
    return this.api.get<NotificationDto[]>('notifications', { params: { limit, unreadOnly: unreadOnly || null } });
  }

  unreadCount(): Observable<{ count: number }> {
    return this.api.get<{ count: number }>('notifications/unread-count', { silent: true });
  }

  markRead(id: Guid): Observable<void> {
    return this.api.post<void>(`notifications/${encodeURIComponent(id)}/read`, {});
  }

  acknowledge(id: Guid): Observable<void> {
    return this.api.post<void>(`notifications/${encodeURIComponent(id)}/ack`, {});
  }

  markAllRead(): Observable<void> {
    return this.api.post<void>('notifications/read-all', {});
  }
}
