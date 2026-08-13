# Examifo Desktop

Examifo Desktop is a .NET MAUI desktop application for taking and managing exams with support for offline-first exam execution, local persistence, timed attempts, submission handling, and synchronization.

## Project Status

🚧 **Under Development**

The current version contains the initial application structure and a working local/mock login and exam flow. API integration and the complete offline synchronization system will be implemented as the project progresses.

## Technology Stack

* **.NET MAUI**
* **C#**
* **XAML**
* **SQLite** — planned for local exam and attempt storage
* **HTTPS + JSON API** — planned for backend integration

## Project Structure

```text
Examifo_Desktop
│
├── Domain
│   ├── Enums
│   └── Models
│
├── Infrastructure
│   ├── Api
│   │   ├── Clients
│   │   └── DTOs
│   ├── Persistence
│   ├── Security
│   └── Sync
│
├── Pages
│   ├── LoginPage
│   ├── ExamListPage
│   ├── ExamPage
│   └── SubmissionPage
│
├── Services
│   ├── AuthenticationService
│   ├── ExamService
│   └── SubmissionService
│
├── ViewModels
│
├── Platforms
├── Resources
│
├── App.xaml
├── AppShell.xaml
└── MauiProgram.cs
```

## Current Workflow

```text
Student
   ↓
Login
   ↓
Local/Mock Authentication
   ↓
Exam List
   ↓
Select Exam
   ↓
Exam Page
   ↓
Questions + Timer
   ↓
Submission
```

## Planned Architecture

```text
MAUI UI
   ↓
Services
   ↓
API Clients
   ↓
/api/v1 Backend
```

For offline operation:

```text
MAUI UI
   ↓
Exam Engine
   ↓
Encrypted SQLite
   ↓
Local Outbox
   ↓
Sync Worker
   ↓
Backend API
```

## Current Features

* .NET MAUI application structure
* XAML-based UI
* Login screen
* Local/mock authentication
* Exam list
* Demo exam
* Question models
* Multiple question-type definitions
* Exam timer foundation
* Submission models and service structure
* API client structure
* Persistence, security, and synchronization structure

## Planned Features

* Real API authentication
* Exam retrieval from backend
* Encrypted local SQLite storage
* Offline exam execution
* Automatic answer persistence
* Exam deadline and locking
* Final submission generation
* Local outbox for pending submissions
* Background synchronization
* Submission status handling
* Server-side submission validation
* Additional Examifo question types

## Running the Project

### Requirements

* Visual Studio with .NET MAUI workload
* .NET SDK compatible with the project
* Windows development environment for Windows desktop testing

### Run

Clone the repository:

```bash
git clone https://github.com/HudaYasin11/Examifo_Desktop.git
```

Open the solution/project in Visual Studio and run the project using the desired MAUI target, such as **Windows Machine**.

## Development Notes

The current authentication is **mock authentication for development only**. It is not intended for production use.

API integration will be added once the backend API contract, endpoints, and request/response formats are available.

## Repository

GitHub:

https://github.com/HudaYasin11/Examifo_Desktop

## License

This project is currently being developed as part of the Examifo project. License and distribution terms have not yet been defined.
