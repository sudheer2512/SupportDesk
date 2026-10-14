import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid } from '../models/common';
import {
  ErpConnectionTestDto,
  ErpIntegrationDto,
  ErpRecordViewDto,
  ErpSyncResultDto,
  ErpSystem,
  IntegrationLogDto,
  SaveErpIntegrationRequest,
} from '../models/erp';

const enc = encodeURIComponent;

/**
 * Wings ERP / Wings Books integration (`/integrations/erp…`): ERP record lookup for the ticket context panel and the
 * administration endpoints (settings, connection test, sync now, integration log) which need `admin.integrations`.
 */
@Injectable({ providedIn: 'root' })
export class ErpApi {
  private readonly api = inject(ApiClient);

  record(type: string, id: Guid, system: ErpSystem = 'WingsErp'): Observable<ErpRecordViewDto> {
    return this.api.get<ErpRecordViewDto>(`integrations/erp/records/${enc(type)}/${enc(id)}`, { params: { system } });
  }

  settings(system: ErpSystem): Observable<ErpIntegrationDto> {
    return this.api.get<ErpIntegrationDto>('integrations/erp/settings', { params: { system } });
  }

  saveSettings(system: ErpSystem, request: SaveErpIntegrationRequest): Observable<ErpIntegrationDto> {
    return this.api.put<ErpIntegrationDto>(`integrations/erp/settings?system=${enc(system)}`, request, { silent: true });
  }

  test(system: ErpSystem): Observable<ErpConnectionTestDto> {
    return this.api.post<ErpConnectionTestDto>(`integrations/erp/test?system=${enc(system)}`);
  }

  sync(system: ErpSystem): Observable<ErpSyncResultDto> {
    return this.api.post<ErpSyncResultDto>(`integrations/erp/sync?system=${enc(system)}`, {}, { silent: true });
  }

  logs(system: ErpSystem, operation?: string | null, limit = 100): Observable<IntegrationLogDto[]> {
    return this.api.get<IntegrationLogDto[]>('integrations/erp/logs', { params: { system, operation, limit } });
  }
}
