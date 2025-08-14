// Microsoft Graph Calendar Integration for Healthcare Appointments

const { ClientSecretCredential } = require('@azure/identity');
const { Client } = require('@microsoft/microsoft-graph-client');
require('dotenv').config();

// Calendar management plugin for healthcare appointments
class SchedulingPlugin {
    constructor(calendarUserId = null) {
        // Authentication setup
        const credential = new ClientSecretCredential(
            process.env.GRAPH_TENANT_ID,
            process.env.GRAPH_CLIENT_ID,
            process.env.GRAPH_CLIENT_SECRET
        );

        // Microsoft Graph client setup
        this.graphClient = Client.initWithMiddleware({
            authProvider: {
                getAccessToken: async () => {
                    const token = await credential.getToken('https://graph.microsoft.com/.default');
                    console.log('[SchedulingPlugin] Token acquired successfully');
                    return token.token;
                }
            }
        });

        // Set calendar user ID - use provided parameter or fall back to environment variable
        this.calendarUserId = calendarUserId || process.env.GRAPH_USER_ID;

        console.log('[SchedulingPlugin] Initialized with real calendar mode');
        console.log('[SchedulingPlugin] Client ID:', this.maskValue(process.env.GRAPH_CLIENT_ID));
        console.log('[SchedulingPlugin] Tenant ID:', this.maskValue(process.env.GRAPH_TENANT_ID));
        console.log('[SchedulingPlugin] User ID:', this.maskValue(this.calendarUserId));
    }

    // Update the calendar user ID dynamically
    setCalendarUserId(newUserId) {
        if (!newUserId) {
            console.warn('[SchedulingPlugin] Warning: Attempting to set empty calendar user ID');
            return false;
        }

        const previousUserId = this.calendarUserId;
        this.calendarUserId = newUserId;

        console.log('[SchedulingPlugin] Calendar user ID updated');
        console.log('[SchedulingPlugin] Previous User ID:', this.maskValue(previousUserId));
        console.log('[SchedulingPlugin] New User ID:', this.maskValue(newUserId));

        return true;
    }

    // Get the current calendar user ID
    getCalendarUserId() {
        return this.calendarUserId;
    }

    // Mask sensitive values in logs for security
    maskValue(value) {
        if (!value || value.length < 8) {
            return '***masked***';
        }
        return `${value.substring(0, 4)}...${value.substring(value.length - 4)}`;
    }

    // Format Date object for Microsoft Graph API (timezone-aware)
    formatDateTimeForGraph(date) {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        const hours = String(date.getHours()).padStart(2, '0');
        const minutes = String(date.getMinutes()).padStart(2, '0');
        const seconds = String(date.getSeconds()).padStart(2, '0');

        return `${year}-${month}-${day}T${hours}:${minutes}:${seconds}`;
    }

    // Find available appointment slots for a given date
    async findAvailability(date) {
        console.log(`[SchedulingPlugin] Checking availability for: ${date}`);
        try {
            const userTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
            console.log('[SchedulingPlugin] User timezone:', userTimeZone);

            // Create start and end times for the day
            const startDateTime = `${date}T00:00:00`;
            const endDateTime = `${date}T23:59:59`;

            const events = await this.graphClient
                .api(`/users/${this.calendarUserId}/calendar/calendarView`)
                .query({
                    startDateTime: startDateTime,
                    endDateTime: endDateTime
                })
                .orderby('start/dateTime')
                .get();

            // Standard business hours for appointments
            const businessHours = [
                { time: '09:00', label: '9:00 AM' },
                { time: '10:00', label: '10:00 AM' },
                { time: '10:30', label: '10:30 AM' },
                { time: '11:00', label: '11:00 AM' },
                { time: '14:00', label: '2:00 PM' },
                { time: '15:00', label: '3:00 PM' },
                { time: '16:00', label: '4:00 PM' },
                { time: '17:00', label: '5:00 PM' }
            ];

            // Identify booked time slots
            const bookedSlots = events.value.map(event => {
                // Convert event times to local format
                let localTime;
                if (event.start.timeZone === 'UTC') {
                    // Convert UTC to local timezone
                    const utcDate = new Date(event.start.dateTime + (event.start.dateTime.endsWith('Z') ? '' : 'Z'));
                    localTime = utcDate.toLocaleTimeString('en-US', {
                        hour12: false,
                        hour: '2-digit',
                        minute: '2-digit',
                        timeZone: userTimeZone
                    });
                } else {
                    // Use event's specific timezone
                    const eventDate = new Date(event.start.dateTime);
                    localTime = eventDate.toLocaleTimeString('en-US', {
                        hour12: false,
                        hour: '2-digit',
                        minute: '2-digit',
                        timeZone: event.start.timeZone
                    });
                }

                console.log(`[SchedulingPlugin] Booked slot: ${localTime} (from ${event.start.dateTime} ${event.start.timeZone})`);
                return localTime;
            });

            // Filter out booked slots
            const availableSlots = businessHours.filter(slot =>
                !bookedSlots.includes(slot.time)
            );

            if (availableSlots.length === 0) {
                return `Sorry, no appointment slots are available for ${date}. All time slots are booked.`;
            }

            const slotLabels = availableSlots.map(slot => slot.label).join(', ');
            const availabilityMsg = `For ${date}, available slots are at ${slotLabels}.`;
            console.log(`[SchedulingPlugin] Found availability: ${availabilityMsg}`);
            return availabilityMsg;
        } catch (error) {
            console.error('[SchedulingPlugin] Error finding availability:', error);
            console.error('[SchedulingPlugin] Error details:', {
                statusCode: error.statusCode,
                code: error.code,
                message: error.message,
                requestId: error.requestId
            });

            // Handle specific errors
            if (error.statusCode === 401) {
                return 'Authentication failed when checking availability. Please check your credentials.';
            } else if (error.statusCode === 403) {
                return 'Permission denied when checking availability. Please check app permissions in Azure AD.';
            } else if (error.statusCode === 404) {
                return 'User or calendar not found when checking availability. Please check the GRAPH_USER_ID setting.';
            } else {
                return `Sorry, I encountered an error while checking the calendar: ${error.message || 'Unknown error'}`;
            }
        }
    }

    // Create a new appointment in the calendar
    async createAppointment(appointmentDateTime, patientName) {
        console.log(`[SchedulingPlugin] Creating real appointment for ${patientName} at ${appointmentDateTime}`);
        console.log('[SchedulingPlugin] Target user:', this.calendarUserId);
        console.log('[SchedulingPlugin] Appointment time:', appointmentDateTime);

        try {
            // Get the user's timezone
            const userTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
            console.log('[SchedulingPlugin] User timezone:', userTimeZone);

            // Parse appointment time and create end time (1 hour later)
            let startDateTime, endDateTime;

            if (appointmentDateTime.includes('T')) {
                const cleanDateTime = appointmentDateTime.replace('Z', '');
                const [datePart, timePart] = cleanDateTime.split('T');
                const [hourStr, minuteStr] = timePart.split(':');
                const hour = parseInt(hourStr);
                const minute = parseInt(minuteStr);

                // Create end time by adding 1 hour
                let endHour = hour + 1;
                let endMinute = minute;

                // Handle hour overflow
                if (endHour >= 24) {
                    endHour = endHour - 24;
                    // For simplicity, assume same day (could be enhanced for day overflow)
                }

                startDateTime = cleanDateTime;
                endDateTime = `${datePart}T${String(endHour).padStart(2, '0')}:${String(endMinute).padStart(2, '0')}:00`;
            } else {
                // If it's just a date, add default time
                startDateTime = appointmentDateTime + 'T09:00:00';
                endDateTime = appointmentDateTime + 'T10:00:00';
            }

            console.log('[SchedulingPlugin] Start time:', startDateTime);
            console.log('[SchedulingPlugin] End time:', endDateTime);

            // Create the event object with the user's timezone
            const event = {
                subject: `Appointment for ${patientName}`,
                start: {
                    dateTime: startDateTime,
                    timeZone: userTimeZone
                },
                end: {
                    dateTime: endDateTime,
                    timeZone: userTimeZone
                },
                attendees: []
            };

            console.log('[SchedulingPlugin] Event object:', JSON.stringify(event, null, 2));

            // Make the API call to create the event
            const result = await this.graphClient.api(`/users/${this.calendarUserId}/events`).post(event);

            // For confirmation message, parse the original time correctly
            const [datePart, timePart] = startDateTime.split('T');
            const [hour, minute] = timePart.split(':').map(Number);

            // Create a date for display formatting
            const displayDate = new Date(`${datePart}T${timePart}`);

            const formattedTime = displayDate.toLocaleTimeString('en-US', {
                hour: 'numeric',
                minute: '2-digit',
                hour12: true
            });
            const formattedDate = displayDate.toLocaleDateString('en-US', {
                weekday: 'long',
                year: 'numeric',
                month: 'long',
                day: 'numeric'
            });

            const confirmationMsg = `Perfect! I've successfully booked a real appointment for ${patientName} on ${formattedDate} at ${formattedTime}. Event ID: ${result.id}`;
            console.log(`[SchedulingPlugin] Successfully created real event with ID: ${result.id}`);
            return confirmationMsg;
        } catch (error) {
            console.error('[SchedulingPlugin] Error creating real appointment:', error);
            console.error('[SchedulingPlugin] Error details:', {
                statusCode: error.statusCode,
                code: error.code,
                message: error.message,
                requestId: error.requestId
            });

            // Handle different types of errors with specific messages
            if (error.statusCode === 401) {
                return 'Authentication failed when creating the appointment. Please check your credentials and permissions.';
            } else if (error.statusCode === 403) {
                return 'Permission denied when creating the appointment. Please check app permissions in Azure AD.';
            } else if (error.statusCode === 404) {
                return 'User or calendar not found when creating the appointment. Please check the GRAPH_USER_ID setting.';
            } else if (error.statusCode === 400) {
                return 'The appointment details are invalid. Please try with different date/time.';
            } else {
                return `Sorry, I encountered an error while booking the appointment: ${error.message || 'Unknown error'}`;
            }
        }
    }

    // List appointments for a specific date
    async listAppointments(date) {
        console.log(`[SchedulingPlugin] Listing appointments for: ${date}`);

        try {
            console.log('[SchedulingPlugin] Checking Microsoft Graph calendar...');
            const userTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
            console.log('[SchedulingPlugin] User timezone:', userTimeZone);

            const startDateTime = `${date}T00:00:00`;
            const endDateTime = `${date}T23:59:59`;

            const events = await this.graphClient
                .api(`/users/${this.calendarUserId}/calendar/calendarView`)
                .query({
                    startDateTime: startDateTime,
                    endDateTime: endDateTime
                })
                .orderby('start/dateTime')
                .get();

            if (events.value.length === 0) {
                return `No appointments found for ${date}.`;
            }

            let appointmentsList = `Found ${events.value.length} appointment(s) for ${date}:\n`;
            events.value.forEach((event, index) => {
                // Parse the event start time and display it properly
                console.log(`[SchedulingPlugin] Event ${index + 1}:`, {
                    subject: event.subject,
                    startDateTime: event.start.dateTime,
                    timeZone: event.start.timeZone
                });

                // Microsoft Graph returns times in UTC when we query, even if they were created with a timezone
                // So we need to convert UTC back to the user's local timezone
                let startTime;
                if (event.start.timeZone === 'UTC') {
                    // Convert UTC to user's local timezone
                    const utcDate = new Date(event.start.dateTime + (event.start.dateTime.endsWith('Z') ? '' : 'Z'));
                    startTime = utcDate.toLocaleTimeString('en-US', {
                        hour: 'numeric',
                        minute: '2-digit',
                        hour12: true,
                        timeZone: userTimeZone
                    });
                } else {
                    // If it has a specific timezone, use it
                    const eventDate = new Date(event.start.dateTime);
                    startTime = eventDate.toLocaleTimeString('en-US', {
                        hour: 'numeric',
                        minute: '2-digit',
                        hour12: true,
                        timeZone: event.start.timeZone
                    });
                }

                appointmentsList += `${index + 1}. ${event.subject} at ${startTime}\n`;
            });

            console.log(`[SchedulingPlugin] Found ${events.value.length} appointments`);
            return appointmentsList;
        } catch (error) {
            console.error('[SchedulingPlugin] Error listing appointments:', error);
            console.error('[SchedulingPlugin] Error details:', {
                statusCode: error.statusCode,
                code: error.code,
                message: error.message,
                requestId: error.requestId
            });

            // Handle specific errors for real calendar mode
            if (error.statusCode === 401) {
                return 'Authentication failed when accessing the calendar. Please check your credentials.';
            } else if (error.statusCode === 403) {
                return 'Permission denied when accessing the calendar. Please check app permissions in Azure AD.';
            } else if (error.statusCode === 404) {
                return 'User or calendar not found. Please check the GRAPH_USER_ID setting.';
            } else {
                return `Sorry, I encountered an error while checking the calendar: ${error.message || 'Unknown error'}`;
            }
        }
    }

    // Test authentication and basic Graph API access
    async testAuthentication() {
        console.log('[SchedulingPlugin] Testing authentication...');
        console.log('[SchedulingPlugin] Client ID:', this.maskValue(process.env.GRAPH_CLIENT_ID));
        console.log('[SchedulingPlugin] Tenant ID:', this.maskValue(process.env.GRAPH_TENANT_ID));
        console.log('[SchedulingPlugin] User ID:', this.maskValue(process.env.GRAPH_USER_ID));

        try {
            // Test basic user access
            console.log('[SchedulingPlugin] Testing user access...');
            const user = await this.graphClient.api(`/users/${this.calendarUserId}`).get();
            console.log(`[SchedulingPlugin] Successfully authenticated. User: ${user.displayName} (${user.mail})`);

            // Test calendar access
            console.log('[SchedulingPlugin] Testing calendar access...');
            const calendars = await this.graphClient.api(`/users/${this.calendarUserId}/calendars`).get();
            console.log(`[SchedulingPlugin] Found ${calendars.value.length} calendars`);

            return true;
        } catch (error) {
            console.error('[SchedulingPlugin] Authentication test failed:');
            console.error('[SchedulingPlugin] Status Code:', error.statusCode);
            console.error('[SchedulingPlugin] Error Code:', error.code);
            console.error('[SchedulingPlugin] Error Message:', error.message);
            return false;
        }
    }

    // Cancel an appointment by searching and deleting it
    async cancelAppointment(date, time) {
        console.log(`[SchedulingPlugin] Canceling appointment on ${date} at ${time}`);
        console.log('[SchedulingPlugin] Target user:', this.calendarUserId);

        try {
            // Normalize time format for searching
            let normalizedTime = time;
            if (time.includes('AM') || time.includes('PM')) {
                // Convert 12-hour to 24-hour format
                const [timePart, meridiem] = time.split(/\s*(AM|PM)\s*/i);
                let [hour, minute = '00'] = timePart.split(':').map(str => str.trim());
                hour = parseInt(hour);

                if (meridiem.toUpperCase() === 'PM' && hour !== 12) {
                    hour += 12;
                } else if (meridiem.toUpperCase() === 'AM' && hour === 12) {
                    hour = 0;
                }

                normalizedTime = `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
            }

            console.log(`[SchedulingPlugin] Searching for appointment at normalized time: ${normalizedTime}`);

            // Calculate date range for the specified day
            const userTimeZone = 'Asia/Calcutta';
            const startDateTime = `${date}T00:00:00`;
            const endDateTime = `${date}T23:59:59`;

            // Search for appointments on the specified date
            const events = await this.graphClient
                .api(`/users/${this.calendarUserId}/events`)
                .filter(`start/dateTime ge '${startDateTime}' and start/dateTime le '${endDateTime}'`)
                .orderby('start/dateTime')
                .get();

            if (events.value.length === 0) {
                return `No appointments found for ${date}.`;
            }

            // Find the appointment that matches the specified time
            let targetEvent = null;
            for (const event of events.value) {
                let eventTime;
                if (event.start.timeZone === 'UTC') {
                    // Convert UTC to user's local timezone
                    const utcDate = new Date(event.start.dateTime + (event.start.dateTime.endsWith('Z') ? '' : 'Z'));
                    eventTime = utcDate.toLocaleTimeString('en-US', {
                        hour12: false,
                        hour: '2-digit',
                        minute: '2-digit',
                        timeZone: userTimeZone
                    });
                } else {
                    // If it has a specific timezone, use it
                    const eventDate = new Date(event.start.dateTime);
                    eventTime = eventDate.toLocaleTimeString('en-US', {
                        hour12: false,
                        hour: '2-digit',
                        minute: '2-digit',
                        timeZone: event.start.timeZone
                    });
                }

                console.log(`[SchedulingPlugin] Checking event: ${event.subject} at ${eventTime} (normalized: ${normalizedTime})`);

                if (eventTime === normalizedTime) {
                    targetEvent = event;
                    break;
                }
            }

            if (!targetEvent) {
                const availableTimes = events.value.map(event => {
                    let eventTime;
                    if (event.start.timeZone === 'UTC') {
                        const utcDate = new Date(event.start.dateTime + (event.start.dateTime.endsWith('Z') ? '' : 'Z'));
                        eventTime = utcDate.toLocaleTimeString('en-US', {
                            hour: 'numeric',
                            minute: '2-digit',
                            hour12: true,
                            timeZone: userTimeZone
                        });
                    } else {
                        const eventDate = new Date(event.start.dateTime);
                        eventTime = eventDate.toLocaleTimeString('en-US', {
                            hour: 'numeric',
                            minute: '2-digit',
                            hour12: true,
                            timeZone: event.start.timeZone
                        });
                    }
                    return `${eventTime}`;
                }).join(', ');

                return `No appointment found at ${time} on ${date}. Available appointments: ${availableTimes}`;
            }

            // Delete the found appointment
            await this.graphClient.api(`/users/${this.calendarUserId}/events/${targetEvent.id}`).delete();

            // Format the confirmation message
            let displayTime;
            if (targetEvent.start.timeZone === 'UTC') {
                const utcDate = new Date(targetEvent.start.dateTime + (targetEvent.start.dateTime.endsWith('Z') ? '' : 'Z'));
                displayTime = utcDate.toLocaleTimeString('en-US', {
                    hour: 'numeric',
                    minute: '2-digit',
                    hour12: true,
                    timeZone: userTimeZone
                });
            } else {
                const eventDate = new Date(targetEvent.start.dateTime);
                displayTime = eventDate.toLocaleTimeString('en-US', {
                    hour: 'numeric',
                    minute: '2-digit',
                    hour12: true,
                    timeZone: targetEvent.start.timeZone
                });
            }

            const displayDate = new Date(date).toLocaleDateString('en-US', {
                weekday: 'long',
                year: 'numeric',
                month: 'long',
                day: 'numeric'
            });

            console.log(`[SchedulingPlugin] Successfully canceled appointment: ${targetEvent.subject} on ${displayDate} at ${displayTime}`);
            return `Successfully canceled your appointment on ${displayDate} at ${displayTime}.`;

        } catch (error) {
            console.error('[SchedulingPlugin] Error canceling appointment:', error);
            console.error('[SchedulingPlugin] Error details:', {
                statusCode: error.statusCode,
                code: error.code,
                message: error.message,
                requestId: error.requestId
            });

            if (error.statusCode === 401) {
                return 'Authentication failed when accessing the calendar. Please check your credentials.';
            } else if (error.statusCode === 403) {
                return 'Permission denied when accessing the calendar. Please check app permissions in Azure AD.';
            } else if (error.statusCode === 404) {
                return 'Appointment not found or user/calendar not found.';
            } else {
                return `Sorry, I encountered an error while canceling the appointment: ${error.message || 'Unknown error'}`;
            }
        }
    }

    // Reschedule appointment (cancel old, create new)
    async rescheduleAppointment(originalDate, originalTime, newDateTime, patientName = 'Patient') {
        console.log(`[SchedulingPlugin] Rescheduling appointment from ${originalDate} ${originalTime} to ${newDateTime}`);

        try {
            // First, cancel the original appointment
            const cancelResult = await this.cancelAppointment(originalDate, originalTime);

            if (!cancelResult.includes('Successfully canceled')) {
                // If cancellation failed, don't proceed with creating new appointment
                return `Failed to reschedule: ${cancelResult}`;
            }

            // Then, create the new appointment
            const createResult = await this.createAppointment(newDateTime, patientName);

            if (createResult.includes('successfully booked')) {
                return `Successfully rescheduled your appointment. ${cancelResult} ${createResult}`;
            } else {
                // If new appointment creation failed, we have a problem because old one is already canceled
                return `Warning: Original appointment was canceled but failed to create new appointment: ${createResult}. Please book a new appointment manually.`;
            }

        } catch (error) {
            console.error('[SchedulingPlugin] Error rescheduling appointment:', error);
            return `Sorry, I encountered an error while rescheduling the appointment: ${error.message || 'Unknown error'}`;
        }
    }
}

module.exports.SchedulingPlugin = SchedulingPlugin;