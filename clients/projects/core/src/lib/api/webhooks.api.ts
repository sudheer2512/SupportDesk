import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { SaveWebhookRequest, WebhookDeliveryDto, WebhookDto, WebhookEventTypesDto } from '../models/automation';
import { Guid, PageResult } from '../models/common';

const id = encodeURIComponent;

/**
 * Outbound webhook subscriptions (`/webhook-subscriptions`, `admin.integrations`): signed event deliveries
 * (X-SupportDesk-Signature: sha256=HMAC), delivery log, redelivery and test ping.
 */
@Injectable({ providedIn: 'root' })
export class WebhooksApi {
  private readonly api = inject(ApiClient);

  list(): Observable<WebhookDto[]> {
    return this.api.get<WebhookDto[]>('webhook-subscriptions');
  }

  eventTypes(): Observable<WebhookEventTypesDto> {
    return this.api.get<WebhookEventTypesDto>('webhook-subscriptions/event-types');
  }

  get(webhookId: Guid): Observable<WebhookDto> {
    return this.api.get<WebhookDto>(`webhook-subscriptions/${id(webhookId)}`);
  }

  /** The response carries the signing secret once. */
  create(request: SaveWebhookRequest): Observable<WebhookDto> {
    return this.api.post<WebhookDto>('webhook-subscriptions', request, { silent: true });
  }

  update(webhookId: Guid, request: SaveWebhookRequest): Observable<WebhookDto> {
    return this.api.put<WebhookDto>(`webhook-subscriptions/${id(webhookId)}`, request, { silent: true });
  }

  delete(webhookId: Guid): Observable<void> {
    return this.api.delete<void>(`webhook-subscriptions/${id(webhookId)}`);
  }

  setEnabled(webhookId: Guid, enabled: boolean): Observable<WebhookDto> {
    return this.api.post<WebhookDto>(`webhook-subscriptions/${id(webhookId)}/${enabled ? 'enable' : 'disable'}`, {});
  }

  /** New signing secret (returned once). */
  rotateSecret(webhookId: Guid): Observable<WebhookDto> {
    return this.api.post<WebhookDto>(`webhook-subscriptions/${id(webhookId)}/rotate-secret`, {});
  }

  deliveries(webhookId: Guid, query: { status?: string | null; page?: number; pageSize?: number } = {}): Observable<PageResult<WebhookDeliveryDto>> {
    return this.api.get<PageResult<WebhookDeliveryDto>>(`webhook-subscriptions/${id(webhookId)}/deliveries`, { params: query });
  }

  delivery(webhookId: Guid, deliveryId: Guid): Observable<WebhookDeliveryDto> {
    return this.api.get<WebhookDeliveryDto>(`webhook-subscriptions/${id(webhookId)}/deliveries/${id(deliveryId)}`);
  }

  redeliver(webhookId: Guid, deliveryId: Guid): Observable<WebhookDeliveryDto> {
    return this.api.post<WebhookDeliveryDto>(`webhook-subscriptions/${id(webhookId)}/deliveries/${id(deliveryId)}/redeliver`, {}, { silent: true });
  }

  /** Sends a signed "webhook.ping" now and returns the delivery result. */
  ping(webhookId: Guid): Observable<WebhookDeliveryDto> {
    return this.api.post<WebhookDeliveryDto>(`webhook-subscriptions/${id(webhookId)}/ping`, {}, { silent: true });
  }
}
