-- Warning: column statistics not supported by the server.
-- MySQL dump 10.13  Distrib 8.4.9, for Win64 (x86_64)
--
-- Host: 127.0.0.1    Database: uvss_service
-- ------------------------------------------------------
-- Server version	5.5.5-10.4.32-MariaDB

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Table structure for table `__efmigrationshistory`
--

DROP TABLE IF EXISTS `__efmigrationshistory`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `__efmigrationshistory` (
  `MigrationId` varchar(150) NOT NULL,
  `ProductVersion` varchar(32) NOT NULL,
  PRIMARY KEY (`MigrationId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `__efmigrationshistory`
--

LOCK TABLES `__efmigrationshistory` WRITE;
/*!40000 ALTER TABLE `__efmigrationshistory` DISABLE KEYS */;
INSERT INTO `__efmigrationshistory` VALUES ('20260911175235_InitialLaneSetup','9.0.0'),('20260911232236_RemoveUvssCameraIp','9.0.0'),('20260912003238_MoveLaneConfigToDatabase','9.0.0'),('20260912013934_RemoveOverviewCamera','9.0.0'),('20260912023148_AddScanEvents','9.0.0');
/*!40000 ALTER TABLE `__efmigrationshistory` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `anprcameras`
--

DROP TABLE IF EXISTS `anprcameras`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `anprcameras` (
  `Id` int(11) NOT NULL AUTO_INCREMENT,
  `LaneId` longtext NOT NULL,
  `CameraName` longtext NOT NULL,
  `Ip` longtext NOT NULL,
  `Username` longtext NOT NULL,
  `Password` longtext NOT NULL,
  `Enabled` tinyint(1) NOT NULL DEFAULT 0,
  `Source` longtext NOT NULL,
  `TestImageDir` longtext NOT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `anprcameras`
--

LOCK TABLES `anprcameras` WRITE;
/*!40000 ALTER TABLE `anprcameras` DISABLE KEYS */;
INSERT INTO `anprcameras` VALUES (1,'lane1','lane1_anpr','192.168.1.111','admin','admin@123',1,'axis','D:/bhikaji/bhikajianpr/uvss-service/uvss1.mp4');
/*!40000 ALTER TABLE `anprcameras` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `controllers`
--

DROP TABLE IF EXISTS `controllers`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `controllers` (
  `Id` int(11) NOT NULL AUTO_INCREMENT,
  `LaneId` longtext NOT NULL,
  `GateName` longtext NOT NULL,
  `Ip` longtext NOT NULL,
  `DebounceMs` int(11) NOT NULL DEFAULT 0,
  `Enabled` tinyint(1) NOT NULL DEFAULT 0,
  `InterlockSource` longtext NOT NULL,
  `LoopPositionsMetres` longtext NOT NULL,
  `SimulatedIdleSeconds` double NOT NULL DEFAULT 0,
  `SimulatedSegmentSeconds` longtext NOT NULL,
  `TcpPort` int(11) NOT NULL DEFAULT 0,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `controllers`
--

LOCK TABLES `controllers` WRITE;
/*!40000 ALTER TABLE `controllers` DISABLE KEYS */;
INSERT INTO `controllers` VALUES (2,'lane1','entry_gate','192.168.1.151',50,1,'tcp','0,3',25,'6.0',6000);
/*!40000 ALTER TABLE `controllers` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `drivercameras`
--

DROP TABLE IF EXISTS `drivercameras`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `drivercameras` (
  `Id` int(11) NOT NULL AUTO_INCREMENT,
  `LaneId` longtext NOT NULL,
  `CameraName` longtext NOT NULL,
  `Ip` longtext NOT NULL,
  `Username` longtext NOT NULL,
  `Password` longtext NOT NULL,
  `Enabled` tinyint(1) NOT NULL DEFAULT 0,
  `Source` longtext NOT NULL,
  `TestImageDir` longtext NOT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `drivercameras`
--

LOCK TABLES `drivercameras` WRITE;
/*!40000 ALTER TABLE `drivercameras` DISABLE KEYS */;
INSERT INTO `drivercameras` VALUES (1,'lane1','driver','192.168.1.110','admin','admin@123',1,'axis','D:/bhikaji/bhikajianpr/uvss-service/uvss1.mp4');
/*!40000 ALTER TABLE `drivercameras` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `scanevents`
--

DROP TABLE IF EXISTS `scanevents`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `scanevents` (
  `Id` bigint(20) NOT NULL AUTO_INCREMENT,
  `Timestamp` datetime(6) NOT NULL,
  `LaneName` longtext NOT NULL,
  `PlateText` longtext NOT NULL,
  `PlateStateName` longtext NOT NULL,
  `SpeedMetresPerSecond` double NOT NULL,
  `PlateTextValid` tinyint(1) NOT NULL,
  `ForeignObjectDetected` tinyint(1) NOT NULL,
  `DriverName` longtext NOT NULL,
  `Admission` int(11) NOT NULL,
  `DossierNotes` longtext NOT NULL,
  `UnderVehicleImagePath` longtext DEFAULT NULL,
  `HighlightedUnderVehicleImagePath` longtext DEFAULT NULL,
  `DriverImagePath` longtext DEFAULT NULL,
  `AnprImagePath` longtext DEFAULT NULL,
  `OverviewImagePath` longtext DEFAULT NULL,
  `PlateCropImagePath` longtext DEFAULT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=5 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `scanevents`
--

LOCK TABLES `scanevents` WRITE;
/*!40000 ALTER TABLE `scanevents` DISABLE KEYS */;
INSERT INTO `scanevents` VALUES (1,'2026-09-12 08:03:17.839327','lane1','DL12CU9692','Delhi',0.48436704553264964,0,0,'Unknown',1,'Scan complete. No anomalies detected. Vehicle not in registry.','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_undervehicle\\2026-09-12\\lane1_20260912_080317_709.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_driver\\2026-09-12\\lane1_20260912_080317_721.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080317_724.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080317_725_platecrop.jpg'),(2,'2026-09-12 08:03:57.328446','lane1','UP16CP1508','Uttar Pradesh',0.4970192842322571,1,1,'Unknown',2,'THREAT DETECTED: foreign-object scan flagged this pass -- manual inspection required.','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_undervehicle\\2026-09-12\\lane1_20260912_080357_306.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_undervehicle\\2026-09-12\\lane1_20260912_080357_307_highlighted.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_driver\\2026-09-12\\lane1_20260912_080357_316.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080357_317.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080357_326_platecrop.jpg'),(3,'2026-09-12 08:04:24.423843','lane1','DL8CBK9364','Delhi',0.49435878131398286,1,0,'Unknown',1,'Scan complete. No anomalies detected. Vehicle not in registry.','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_undervehicle\\2026-09-12\\lane1_20260912_080424_418.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_driver\\2026-09-12\\lane1_20260912_080424_421.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080424_422.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_080424_422_platecrop.jpg'),(4,'2026-09-12 08:46:31.324730','lane1','DL12CB9687','Delhi',0.4755485988079423,0,0,'Unknown',1,'Scan complete. No anomalies detected. Vehicle not in registry.','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_undervehicle\\2026-09-12\\lane1_20260912_084631_311.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_driver\\2026-09-12\\lane1_20260912_084631_314.jpg','D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_084631_314.jpg',NULL,'D:\\bhikaji\\bhikajianpr\\uvss-service\\src\\UvssService\\bin\\Debug\\net8.0\\captured_anpr\\2026-09-12\\lane1_20260912_084631_315_platecrop.jpg');
/*!40000 ALTER TABLE `scanevents` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Table structure for table `uvsscameras`
--

DROP TABLE IF EXISTS `uvsscameras`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `uvsscameras` (
  `Id` int(11) NOT NULL AUTO_INCREMENT,
  `LaneId` longtext NOT NULL,
  `CameraName` longtext NOT NULL,
  `DeviceSerial` longtext NOT NULL,
  `SimulatedFrameHeightPx` int(11) NOT NULL DEFAULT 0,
  `SimulatedFramesPerSecond` int(11) NOT NULL DEFAULT 0,
  `Source` longtext NOT NULL,
  `TestImagePath` longtext NOT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping data for table `uvsscameras`
--

LOCK TABLES `uvsscameras` WRITE;
/*!40000 ALTER TABLE `uvsscameras` DISABLE KEYS */;
INSERT INTO `uvsscameras` VALUES (1,'lane1','lane1_uvss','SIM-0001',75,200,'pylon','D:/bhikaji/bhikajianpr/uvss-service/uvss1.mp4');
/*!40000 ALTER TABLE `uvsscameras` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping routines for database 'uvss_service'
--
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-09-12 12:51:20
