export interface AdminEmployeeStatus {
  employeeId: string;
  isRunning: boolean;
  startedAt: string | null;
  durationSeconds: number;
  stateText: string;
  isAvailable: boolean;
}

export interface AdminSettings {
  baseUrl: string;
  hasAdminPassword: boolean;
  hasAdminApiToken: boolean;
  defaultProjectId: number | null;
  defaultActivityId: number | null;
  employees: AdminEmployee[];
}

export interface AdminEmployee {
  id: string;
  kimaiUserId: number | null;
  displayName: string;
  pin: string | null;
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
}

export interface KimaiUser {
  id: number;
  username: string | null;
  email: string | null;
  displayName: string;
  avatarUrl: string | null;
}
