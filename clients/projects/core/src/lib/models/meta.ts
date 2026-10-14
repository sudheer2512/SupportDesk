import { Guid, IdName, JsonValue } from './common';
import { Availability, StatusCategory } from './enums';

/** Mirrors SupportDesk.Contracts.Meta — everything a ticket form/filter needs (GET /meta/ticket-form). */

export interface TicketFormMetaDto {
  statuses: StatusDto[];
  priorities: string[];
  severities: string[];
  channels: string[];
  departments: DepartmentRefDto[];
  teams: TeamRefDto[];
  agents: AgentRefDto[];
  categories: CategoryRefDto[];
  issueTypes: IdName[];
  products: ProductRefDto[];
  customFields: CustomFieldDto[];
  layouts: LayoutDto[];
  lookups: LookupDto[];
  brands: IdName[];
  tags: string[];
  ticketNumberPrefix: string;
}

export interface StatusDto {
  id: Guid;
  name: string;
  category: StatusCategory;
  color: string;
  sortOrder: number;
  isDefault: boolean;
  pausesSla: boolean;
  isActive: boolean;
  customerLabel: string | null;
  allowedNext: Guid[];
  substatuses: string[];
}

export interface DepartmentRefDto {
  id: Guid;
  name: string;
  code: string;
  visibleInPortal: boolean;
}

export interface TeamRefDto {
  id: Guid;
  name: string;
  departmentId: Guid;
}

export interface AgentRefDto {
  userId: Guid;
  name: string;
  email: string | null;
  avatarUrl: string | null;
  availability: Availability;
  isLightAgent: boolean;
  departmentIds: Guid[];
  teamIds: Guid[];
  openTickets: number;
  capacity: number;
}

export interface CategoryRefDto {
  id: Guid;
  name: string;
  parentId: Guid | null;
  departmentId: Guid | null;
  visibleInPortal: boolean;
}

export interface ProductRefDto {
  id: Guid;
  name: string;
  code: string;
  modules: IdName[];
  versions: IdName[];
}

export interface CustomFieldDto {
  id: Guid;
  entity: string;
  key: string;
  label: string;
  fieldType: string;
  options: JsonValue;
  isRequired: boolean;
  defaultValue: string | null;
  helpText: string | null;
  validationRegex: string | null;
  visibleInPortal: boolean;
  editableInPortal: boolean;
  departmentIds: Guid[];
  sortOrder: number;
  isActive: boolean;
}

export interface LayoutDto {
  id: Guid;
  entity: string;
  name: string;
  kind: string;
  departmentId: Guid | null;
  brandId: Guid | null;
  sections: JsonValue;
  isDefault: boolean;
}

export interface LookupDto {
  type: string;
  value: string;
  label: string;
}
