import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient, ApiRequestOptions } from '../http/api-client';
import { Guid } from '../models/common';
import {
  ChannelAccountDto,
  ChannelProviderDto,
  ChannelTestResultDto,
  CreateChannelAccountRequest,
  InboundResultDto,
  OutboundMessageDto,
  PollResultDto,
  SimulateCallRequest,
  SimulateEmailRequest,
  SimulateMessageRequest,
  SimulateStatusRequest,
  UpdateChannelAccountRequest,
} from '../models/communication';

/** Channel accounts and the channel simulator (`/channels`, requires admin.channels). */
@Injectable({ providedIn: 'root' })
export class ChannelsApi {
  private readonly api = inject(ApiClient);

  list(type?: string | null): Observable<ChannelAccountDto[]> {
    return this.api.get<ChannelAccountDto[]>('channels', { params: { type } });
  }

  providers(): Observable<ChannelProviderDto[]> {
    return this.api.get<ChannelProviderDto[]>('channels/providers');
  }

  get(id: Guid): Observable<ChannelAccountDto> {
    return this.api.get<ChannelAccountDto>(`channels/${encodeURIComponent(id)}`);
  }

  create(request: CreateChannelAccountRequest, options: ApiRequestOptions = {}): Observable<ChannelAccountDto> {
    return this.api.post<ChannelAccountDto>('channels', request, options);
  }

  update(id: Guid, request: UpdateChannelAccountRequest, options: ApiRequestOptions = {}): Observable<ChannelAccountDto> {
    return this.api.patch<ChannelAccountDto>(`channels/${encodeURIComponent(id)}`, request, options);
  }

  /** Deletes an unused channel; channels with history are deactivated instead (`deleted: false`). */
  delete(id: Guid): Observable<{ deleted: boolean }> {
    return this.api.delete<{ deleted: boolean }>(`channels/${encodeURIComponent(id)}`);
  }

  test(id: Guid): Observable<ChannelTestResultDto> {
    return this.api.post<ChannelTestResultDto>(`channels/${encodeURIComponent(id)}/test`, {}, { silent: true });
  }

  rotateWebhookKey(id: Guid): Observable<ChannelAccountDto> {
    return this.api.post<ChannelAccountDto>(`channels/${encodeURIComponent(id)}/rotate-webhook-key`, {});
  }

  /** Polls a mailbox now instead of waiting for the next scheduled poll. */
  poll(id: Guid): Observable<PollResultDto> {
    return this.api.post<PollResultDto>(`channels/${encodeURIComponent(id)}/poll`, {});
  }

  // ───── Simulator ─────

  simulateEmail(id: Guid, request: SimulateEmailRequest): Observable<InboundResultDto> {
    return this.api.post<InboundResultDto>(`channels/${encodeURIComponent(id)}/simulator/email`, request, { silent: true });
  }

  simulateMessage(id: Guid, request: SimulateMessageRequest): Observable<InboundResultDto> {
    return this.api.post<InboundResultDto>(`channels/${encodeURIComponent(id)}/simulator/message`, request, { silent: true });
  }

  simulateCall(id: Guid, request: SimulateCallRequest): Observable<InboundResultDto> {
    return this.api.post<InboundResultDto>(`channels/${encodeURIComponent(id)}/simulator/call`, request, { silent: true });
  }

  simulateStatus(id: Guid, request: SimulateStatusRequest): Observable<InboundResultDto> {
    return this.api.post<InboundResultDto>(`channels/${encodeURIComponent(id)}/simulator/status`, request, { silent: true });
  }

  /** Outbound messages of an account (newest first) with their delivery history. */
  outbound(id: Guid, limit = 50): Observable<OutboundMessageDto[]> {
    return this.api.get<OutboundMessageDto[]>(`channels/${encodeURIComponent(id)}/outbound`, { params: { limit } });
  }

  /** Absolute webhook URL to paste into the provider console. */
  webhookUrl(channel: Pick<ChannelAccountDto, 'webhookPath'>): string | null {
    if (!channel.webhookPath) return null;
    return new URL(channel.webhookPath, globalThis.location?.origin ?? 'http://localhost').toString();
  }
}
