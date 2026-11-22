import com.github.jengelman.gradle.plugins.shadow.tasks.ShadowJar

plugins {
    java
    id("com.gradleup.shadow") version "8.3.5"
}

group = "mofumofu"
version = "1.0.0"

java {
    toolchain {
        languageVersion.set(JavaLanguageVersion.of(21))
    }
}

repositories {
    mavenCentral()
    maven("https://repo.papermc.io/repository/maven-public/")
}

dependencies {
    compileOnly("io.papermc.paper:paper-api:1.21.4-R0.1-SNAPSHOT")
    compileOnly("org.apache.logging.log4j:log4j-core:2.24.3")

    implementation("io.undertow:undertow-core:2.3.18.Final")
    implementation("io.undertow:undertow-websockets-jsr:2.3.18.Final")
    implementation("com.fasterxml.jackson.core:jackson-databind:2.18.2")
    implementation("com.fasterxml.jackson.datatype:jackson-datatype-jsr310:2.18.2")
    implementation("org.yaml:snakeyaml:2.3")

    testImplementation("org.junit.jupiter:junit-jupiter:5.11.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher:1.11.4")
}

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.release.set(21)
}

tasks.withType<Test>().configureEach {
    useJUnitPlatform()
    testLogging {
        events("passed", "failed", "skipped")
    }
}

sourceSets {
    test {
        resources.srcDir(file("../tests/contracts"))
    }
}

tasks.named<ShadowJar>("shadowJar") {
    archiveClassifier.set("")
    archiveFileName.set("NekoProbe.jar")
    destinationDirectory.set(layout.buildDirectory.dir("libs"))
    mergeServiceFiles()
    // Undertow, Jackson and Log4j use service loading and reflection; keep all runtime classes.
    dependencies {
        exclude(dependency("io.papermc.paper:.*:.*"))
        exclude(dependency("org.apache.logging.log4j:.*:.*"))
    }
}

tasks.processResources {
    filesMatching("plugin.yml") {
        expand("version" to project.version)
    }
}

tasks.named<Jar>("jar") {
    archiveClassifier.set("thin")
}

tasks.named("build") {
    dependsOn(tasks.named("shadowJar"))
}

tasks.withType<AbstractArchiveTask>().configureEach {
    isPreserveFileTimestamps = false
    isReproducibleFileOrder = true
}
