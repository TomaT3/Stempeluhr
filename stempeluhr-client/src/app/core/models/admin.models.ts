export interface AdminEmployeeStatus {
  employeeId: string;
  displayName: string;
  isRunning: boolean;
  startedAt: string | null;
  durationSeconds: number;
  state: 'clockedOut' | 'working' | 'paused';
  stateText: string;
  isAvailable: boolean;
  /** Laufende weitere Tätigkeit (null = Standard-Tätigkeit, Pause oder aus). */
  activeTaskLabel?: string | null;
}

export interface AdminSettings {
  baseUrl: string;
  hasAdminPassword: boolean;
  hasAdminApiToken: boolean;
  defaultProjectId: number | null;
  defaultActivityId: number | null;
  pauseActivityId: number | null;
  employees: AdminEmployee[];
}

export interface AdminEmployee {
  id: string;
  kimaiUserId: number | null;
  displayName: string;
  pin: string | null;
  nfcCardId: string | null;
  hasApiToken: boolean;
  apiToken?: string;
  projectId: number | null;
  activityId: number | null;
  color: string;
  imageUrl: string | null;
  description: string | null;
  tags: string[];
  billable: boolean;
  isEnabled: boolean;
  /** Weitere Tätigkeiten (andere Kunden), am Kiosk ohne Ausstempeln wählbar. */
  tasks: AdminEmployeeTask[];
}

export interface AdminEmployeeTask {
  id: string;
  label: string;
  projectId: number | null;
  activityId: number | null;
  billable: boolean;
}

export interface KimaiActivity {
  id: number;
  name: string;
  parentTitle: string | null;
  projectId: number | null;
  visible: boolean;
}

export interface KimaiProject {
  id: number;
  name: string;
  parentTitle: string | null;
  customerId: number | null;
  visible: boolean;
}

export interface KimaiUser {
  id: number;
  username: string | null;
  email: string | null;
  displayName: string;
  avatarUrl: string | null;
}
