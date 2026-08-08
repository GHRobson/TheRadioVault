# Radio Vault v0.28.0-alpha1-buildfix1

## Fixed

- Fixed CS0104 in `MainWindow.xaml.cs`: `PlaybackCoordinator` was ambiguous between the established UI coordinator and a service-layer coordinator.
- The Library Truth Engine is now imported through an explicit type alias rather than importing the entire service implementation namespace globally.

## Scope

- Database schema remains 40.
- Shadow-library parsing and export behaviour are unchanged.
- No live library or audio files are modified.
