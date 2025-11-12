(function () {
  "use strict";
  function controller($scope) {
    var cfg = $scope.model.config || {};
    var vm = this;

    vm.cfg = {
      multiple: cfg.multiple === true,
      onlyImages: cfg.onlyImages !== false,
      startNodeUdi: cfg.startNodeUdi || null,
      startNodeId: (typeof cfg.startNodeId === "number") ? cfg.startNodeId : null
    };

    // Value is a single UDI string (since multiple:false)
    vm.selection = (typeof $scope.model.value === "string") ? $scope.model.value : null;

    $scope.$watch(function () { return vm.selection; }, function (val) {
      $scope.model.value = val || null;
    }, true);
  }
  angular.module("umbraco").controller("Hans.ParamMediaPickerController", controller);
})();
